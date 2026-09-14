import asyncio
import io
import json
import socket
import sys
import uuid
from pathlib import Path

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parents[2]))
from tools.simulator.protocol import Heartbeat, Trigger, encode_input, decode_output
from tools.simulator.state import SimulatorState
from tools.simulator.controller import Simulator, create_device, serve
from pymodbus.client import AsyncModbusTcpClient


def result_registers(trigger, inspection_id=None):
    inspection_id = inspection_id or uuid.UUID("12345678-1234-5678-90ab-cdef01234567")
    data = [8, 1] + encode_input(trigger)[1:11] + encode_input(trigger)[1:11]
    data += [3] + [int.from_bytes(inspection_id.bytes[i:i + 2], "big") for i in range(0, 16, 2)]
    return data + [0, 7, 42]


def test_registers_use_rfc_uuid_high_words_and_fixed_offsets():
    trigger = Trigger(str(uuid.UUID("00112233-4455-6677-8899-aabbccddeeff")), 0x12345678, "PCB-A", "44000062")
    registers = encode_input(trigger)
    assert len(registers) == 38
    assert registers[1:11] == [0x0011, 0x2233, 0x4455, 0x6677, 0x8899, 0xaabb, 0xccdd, 0xeeff, 0x1234, 0x5678]
    assert registers[11:15] == [5, 0x5043, 0x422d, 0x4100]
    assert registers[28:33] == [8, 0x3434, 0x3030, 0x3030, 0x3632]
    output = decode_output(result_registers(trigger))
    assert output.result_identity == trigger.identity
    assert output.inspection_id == "12345678-1234-5678-90ab-cdef01234567"
    assert output.production_sequence == 7


def test_sqlite_pending_survives_restart_and_result_is_counted_once(tmp_path):
    database = tmp_path / "controller.db"
    with SimulatorState(database) as state:
        original = state.begin("PCB-A", "44000062")
    with SimulatorState(database) as state:
        assert state.pending == original
        output = decode_output(result_registers(original))
        assert state.record_result(original, output)
        assert not state.record_result(original, output)
        assert state.result_count == 1
    with SimulatorState(database) as state:
        assert state.pending == original
        assert state.result_count == 1
        state.finish(original)
        new = state.begin("PCB-A", "44000063")
        assert new.session_id != original.session_id
        assert new.sequence == 1


def test_changed_or_unrelated_result_cannot_overwrite_ledger(tmp_path):
    with SimulatorState(tmp_path / "controller.db") as state:
        trigger = state.begin("PCB-A", "44000062")
        state.record_result(trigger, decode_output(result_registers(trigger)))
        with pytest.raises(ValueError, match="changed"):
            state.record_result(trigger, decode_output(result_registers(trigger, uuid.uuid4())))
        stranger = Trigger(str(uuid.uuid4()), 1, "PCB-A", "44000062")
        with pytest.raises(ValueError, match="identity"):
            state.record_result(trigger, decode_output(result_registers(stranger)))
        assert state.result_count == 1
        assert state.pending == trigger


def test_ready_requires_observed_heartbeat_change_and_expires():
    heartbeat = Heartbeat()
    assert not heartbeat.observe(65535, 0)
    assert heartbeat.observe(0, 0.5)
    assert heartbeat.observe(0, 2.49)
    assert not heartbeat.observe(0, 2.5)


def test_sample_list_accepts_ids_only_and_rejects_truth(tmp_path):
    from tools.simulator.__main__ import read_samples
    samples = tmp_path / "samples.jsonl"
    samples.write_text('{"sampleId":"44000062"}\n', encoding="utf-8")
    assert read_samples(samples) == ["44000062"]
    samples.write_text('{"sampleId":"44000062","defects":[]}\n', encoding="utf-8")
    with pytest.raises(ValueError, match="only sampleId"):
        read_samples(samples)


def free_port():
    with socket.socket() as listener:
        listener.bind(("127.0.0.1", 0))
        return listener.getsockname()[1]


class ProtocolStation:
    """Protocol peer only: explicit NotEvaluated, no image/quality claims."""

    def __init__(self, ready=True, ready_after_ack=True):
        self.held = None
        self.history = {}
        self.accepted = 0
        self.dispositions = []
        self.ready = ready
        self.ready_after_ack = ready_after_ack

    async def drive(self, port, server_task):
        client = AsyncModbusTcpClient("127.0.0.1", port=port, timeout=0.25, retries=0)
        for _ in range(100):
            if await client.connect():
                break
            await asyncio.sleep(0.02)
        response = None
        previous_trigger = False
        try:
            while not server_task.done():
                read = await client.read_holding_registers(0, count=38, device_id=1)
                assert not read.isError()
                registers = read.registers
                session = str(uuid.UUID(bytes=b"".join(word.to_bytes(2, "big") for word in registers[1:9])))
                sequence = registers[9] << 16 | registers[10]
                identity = session, sequence
                if registers[0] and not previous_trigger:
                    trigger = Trigger(session, sequence, "PCB-A", "44000062")
                    if self.held:
                        disposition = 2 if identity == self.held.result_identity else 3
                    elif identity in self.history:
                        disposition = 2
                    elif not self.ready:
                        disposition = 4
                    else:
                        disposition = 1
                        self.accepted += 1
                        self.held = decode_output(result_registers(trigger, uuid.uuid4()))
                        self.history[identity] = self.held
                    self.dispositions.append(disposition)
                    response = trigger, disposition
                previous_trigger = bool(registers[0])
                if registers[37] and self.held:
                    assert identity == self.held.result_identity, "ACK must restore the original identity after Busy"
                    self.held = None
                    self.ready = self.ready_after_ack
                stored = self.held or self.history.get(identity)
                words = result_registers(Trigger(*stored.result_identity, "PCB-A", "44000062"), uuid.UUID(stored.inspection_id)) if stored else [0] * 34
                words[0] = (8 if self.held else (1 if self.ready and not registers[37] else 0)) | (2 if registers[0] else 0)
                if response:
                    words[1] = response[1]
                    words[2:12] = encode_input(response[0])[1:11]
                words[33] = int(asyncio.get_running_loop().time() * 10) & 0xffff
                written = await client.write_registers(100, words, device_id=1)
                assert not written.isError()
                await asyncio.sleep(0.01)
        except Exception:
            if not server_task.done():
                raise
        finally:
            client.close()


async def run_wire(state, peer, scenario="normal", count=1, timeout=2, ack_delay=0.15):
    port, events = free_port(), io.StringIO()
    simulator = Simulator(create_device(), state, ["44000062"], "PCB-A", events,
                          scenario=scenario, count=count, timeout=timeout, ack_delay=ack_delay)
    task = asyncio.create_task(serve(simulator, "127.0.0.1", port))
    while not events.getvalue() and not task.done():
        await asyncio.sleep(0.001)
    station_task = asyncio.create_task(peer.drive(port, task))
    try:
        await asyncio.wait_for(task, 6)
    finally:
        await asyncio.wait_for(station_task, 2)
    return [json.loads(line) for line in events.getvalue().splitlines()]


@pytest.mark.parametrize("scenario,expected", [("normal", [1]), ("duplicate-trigger", [1, 2]),
                                               ("busy", [1, 3]), ("lost-ack", [1])])
def test_actual_fc03_fc16_handshake_preserves_one_station_result(tmp_path, scenario, expected):
    peer = ProtocolStation()
    with SimulatorState(tmp_path / "controller.db") as state:
        events = asyncio.run(run_wire(state, peer, scenario))
        assert state.pending is None
        assert state.result_count == 1
        assert peer.accepted == 1
        assert peer.dispositions == expected
        assert [row["kind"] for row in events].count("resultRead") == 1
        assert next(row for row in events if row["kind"] == "resultRead")["resultCode"] == 3
        assert events[-1] == {**events[-1], "kind": "finished", "resultCount": 1}


def test_ack_timeout_restarts_same_identity_without_counting_twice(tmp_path):
    database, peer = tmp_path / "controller.db", ProtocolStation()
    with SimulatorState(database) as state:
        with pytest.raises(TimeoutError, match="Withheld ACK"):
            asyncio.run(run_wire(state, peer, "lost-ack", timeout=0.25, ack_delay=1))
        original = state.pending
        assert state.result_count == 1
    with SimulatorState(database) as state:
        assert state.pending == original
        events = asyncio.run(run_wire(state, peer, "lost-ack"))
        assert state.result_count == 1
        assert state.pending is None
        assert peer.accepted == 1
        read = next(row for row in events if row["kind"] == "resultRead")
        assert read["firstRead"] is False
        assert (read["sessionId"], read["sequence"]) == original.identity


@pytest.mark.parametrize("ready", [True, False])
def test_acknowledged_history_recovers_without_waiting_for_new_valid(tmp_path, ready):
    database, peer = tmp_path / "controller.db", ProtocolStation(ready=ready)
    with SimulatorState(database) as state:
        original = state.begin("PCB-A", "44000062")
        historical = decode_output(result_registers(original))
        state.record_result(original, historical)
        peer.history[original.identity] = historical
    with SimulatorState(database) as state:
        events = asyncio.run(run_wire(state, peer))
        assert state.pending is None
        assert state.result_count == 1
        assert peer.accepted == 0
        assert peer.dispositions == [2]
        assert any(row["kind"] == "historicalDuplicate" for row in events)


def test_last_ack_finishes_when_business_ready_turns_off(tmp_path):
    peer = ProtocolStation(ready_after_ack=False)
    with SimulatorState(tmp_path / "controller.db") as state:
        events = asyncio.run(run_wire(state, peer, timeout=0.5))
        assert not peer.ready
        assert state.pending is None and state.result_count == 1
        assert peer.accepted == 1
        assert events[-1]["kind"] == "finished"


def test_recovered_result_can_be_acked_without_new_operator_session(tmp_path):
    database, peer = tmp_path / "controller.db", ProtocolStation(ready=False, ready_after_ack=False)
    with SimulatorState(database) as state:
        original = state.begin("PCB-A", "44000062")
        peer.held = decode_output(result_registers(original))
        peer.history[original.identity] = peer.held
        state.record_result(original, peer.held)
    with SimulatorState(database) as state:
        events = asyncio.run(run_wire(state, peer, timeout=0.5))
        assert state.pending is None and state.result_count == 1
        assert peer.accepted == 0 and peer.dispositions == []
        assert next(row for row in events if row["kind"] == "resultRead")["firstRead"] is False


def test_retried_trigger_waits_for_old_ack_low_before_new_edge(tmp_path):
    async def check(state):
        trigger = state.begin("PCB-A", "44000062")
        device = create_device()
        old = result_registers(trigger)
        old[0], old[1] = 2, 1
        device.setValues(16, 100, old)
        simulator = Simulator(device, state, [trigger.sample_id], trigger.product_id, io.StringIO(), timeout=0.5)
        simulator.heartbeat.observe(41, 0)

        async def station():
            # A previously acknowledged high must be lowered before a new edge.
            assert device.getValues(3, 0, 1) == [0]
            old[0], old[33] = 0, 43
            device.setValues(16, 100, old)
            while device.getValues(3, 0, 1) != [1]:
                await asyncio.sleep(0.005)
            old[0], old[1], old[33] = 2, 2, 44
            device.setValues(16, 100, old)
            while device.getValues(3, 0, 1) != [0]:
                await asyncio.sleep(0.005)
            old[0], old[33] = 0, 45
            device.setValues(16, 100, old)

        exchange = asyncio.create_task(simulator.exchange_trigger(trigger, (2,)))
        observed = asyncio.create_task(station())
        response, _ = await asyncio.wait_for(asyncio.gather(exchange, observed), 1)
        assert response.disposition == 2

    with SimulatorState(tmp_path / "controller.db") as state:
        asyncio.run(check(state))


async def run_cli(tmp_path, peer, scenario="normal", timeout=2, ack_delay=2, count=1):
    samples, state_file, events_file = (tmp_path / name for name in ("samples.jsonl", "controller.db", "events.jsonl"))
    samples.write_text('{"sampleId":"44000062"}\n', encoding="utf-8")
    port = free_port()
    old_lines = len(events_file.read_text().splitlines()) if events_file.exists() else 0
    process = await asyncio.create_subprocess_exec(sys.executable, "-m", "tools.simulator",
        "--scenario", scenario, "--host", "127.0.0.1", "--port", str(port), "--count", str(count),
        "--product-id", "PCB-A", "--samples", str(samples), "--state", str(state_file),
        "--output", str(events_file), "--timeout", str(timeout), "--ack-delay", str(ack_delay),
        cwd=Path(__file__).resolve().parents[2], stdout=asyncio.subprocess.PIPE, stderr=asyncio.subprocess.PIPE)
    task = asyncio.create_task(process.communicate())
    for _ in range(300):
        if events_file.exists() and len(events_file.read_text().splitlines()) > old_lines:
            break
        if task.done():
            break
        await asyncio.sleep(0.01)
    station_task = asyncio.create_task(peer.drive(port, task))
    try:
        _, error = await asyncio.wait_for(task, 8)
        await asyncio.wait_for(station_task, 2)
        return process.returncode, error.decode()
    finally:
        if process.returncode is None:
            process.kill()
            await process.wait()


def test_cli_normal_two_products_finish_both_ack_cycles(tmp_path):
    peer = ProtocolStation()
    result, error = asyncio.run(run_cli(tmp_path, peer, count=2))
    assert result == 0, error
    with SimulatorState(tmp_path / "controller.db") as state:
        assert state.result_count == 2 and state.pending is None
    assert peer.accepted == 2
    assert [identity[1] for identity in peer.history] == [1, 2]
    assert len({identity[0] for identity in peer.history}) == 1
    events = [json.loads(line) for line in (tmp_path / "events.jsonl").read_text().splitlines()]
    assert [row["resultCount"] for row in events if row["kind"] == "completed"] == [1, 2]


def test_cli_process_restart_completes_pending_and_preserves_events(tmp_path):
    peer = ProtocolStation()
    state_file, events_file = tmp_path / "controller.db", tmp_path / "events.jsonl"
    failed, error = asyncio.run(run_cli(tmp_path, peer, "lost-ack", timeout=0.5))
    assert failed != 0 and "Withheld ACK" in error
    with SimulatorState(state_file) as state:
        pending = state.pending
        assert pending is not None and state.result_count == 1
    succeeded, error = asyncio.run(run_cli(tmp_path, peer, "lost-ack"))
    assert succeeded == 0, error
    with SimulatorState(state_file) as state:
        assert state.pending is None and state.result_count == 1
    events = [json.loads(line) for line in events_file.read_text().splitlines()]
    reads = [row for row in events if row["kind"] == "resultRead"]
    assert [row["firstRead"] for row in reads] == [True, False]
    assert {row["inspectionId"] for row in reads} == {peer.history[pending.identity].inspection_id}
    assert peer.accepted == 1
