import asyncio
import json
import time
from dataclasses import asdict
from datetime import datetime, timezone

from pymodbus.datastore import ModbusDeviceContext, ModbusSequentialDataBlock, ModbusServerContext
from pymodbus.server import ModbusTcpServer

from .protocol import Heartbeat, decode_output, encode_input


def create_device():
    # Context APIs apply the library's internal +1; all callers use PDU addresses.
    return ModbusDeviceContext(hr=ModbusSequentialDataBlock(0, [0] * 135))


class Simulator:
    def __init__(self, device, state, samples, product_prefix, output, *, scenario="normal", count=1,
                 timeout=30, ack_delay=3):
        self.device, self.state = device, state
        self.samples, self.product_prefix = samples, product_prefix
        self.output = output
        self.scenario, self.count = scenario, count
        self.timeout, self.ack_delay = timeout, ack_delay
        self.heartbeat = Heartbeat()

    def event(self, kind, **values):
        self.output.write(json.dumps({"atUtc": datetime.now(timezone.utc).isoformat(), "kind": kind, **values}) + "\n")
        self.output.flush()

    def snapshot(self):
        result = decode_output(self.device.getValues(3, 100, 34))
        fresh = self.heartbeat.observe(result.heartbeat, time.monotonic())
        if fresh and result.fault:
            raise RuntimeError("Station Fault; pending identity retained")
        return result, fresh

    async def wait(self, predicate, description):
        deadline = time.monotonic() + self.timeout
        while True:
            output, fresh = self.snapshot()
            if fresh and predicate(output):
                return output
            if time.monotonic() >= deadline:
                raise TimeoutError(f"Timed out waiting for {description}; pending identity retained")
            await asyncio.sleep(0.02)

    def write_trigger_data(self, trigger):
        self.device.setValues(16, 0, [0])
        self.device.setValues(16, 37, [0])
        self.device.setValues(16, 1, encode_input(trigger)[1:37])

    async def exchange_trigger(self, trigger, dispositions):
        self.write_trigger_data(trigger)
        await self.wait(lambda o: not o.trigger_ack, "old TriggerAck falling edge before trigger")
        self.device.setValues(16, 0, [1])
        self.event("trigger", **asdict(trigger))
        response = await self.wait(lambda o: o.trigger_ack and o.response_identity == trigger.identity, "matching TriggerAck")
        self.device.setValues(16, 0, [0])
        self.event("triggerResponse", identity=trigger.identity, disposition=response.disposition)
        await self.wait(lambda o: not o.trigger_ack, "TriggerAck falling edge")
        if response.disposition not in dispositions:
            raise RuntimeError(f"Trigger rejected or unexpected disposition {response.disposition}")
        return response

    async def inject(self, trigger, original):
        if self.state.injection_done or self.scenario == "normal":
            return
        if self.scenario in ("duplicate-trigger", "busy"):
            probe = trigger if self.scenario == "duplicate-trigger" else self.state.reserve_probe(trigger)
            response = await self.exchange_trigger(probe, (2,) if self.scenario == "duplicate-trigger" else (3,))
            if not response.valid or response.result() != original.result():
                raise ValueError("Scenario changed the held result")
            self.write_trigger_data(trigger)
            self.state.mark_injection()
            self.event("scenarioVerified", scenario=self.scenario, identity=trigger.identity)
        elif self.scenario == "lost-ack":
            # The ledger already committed. Omit ACK once; a restart completes this same identity.
            self.state.mark_injection()
            self.event("ackWithheld", identity=trigger.identity, seconds=self.ack_delay)
            started = time.monotonic()
            while time.monotonic() - started < self.ack_delay:
                current, fresh = self.snapshot()
                if fresh and (not current.valid or current.result() != original.result()):
                    raise ValueError("Held result changed while ACK was withheld")
                if time.monotonic() - started >= self.timeout:
                    raise TimeoutError("Withheld ACK reached timeout; durable pending result retained")
                await asyncio.sleep(0.02)

    async def complete(self, trigger, recovering):
        self.write_trigger_data(trigger)
        # An existing identity can be queried without permission to accept a new product.
        output = await self.wait(lambda o: o.valid or (not o.trigger_ack and
                                 (o.ready or (recovering and not o.busy))), "fresh Ready or pending identity recovery")
        if not output.valid:
            response = await self.exchange_trigger(trigger, (1, 2))
            if response.disposition == 2 and not response.valid:
                self.state.confirm_history(trigger, response)
                self.state.finish(trigger)
                self.event("historicalDuplicate", identity=trigger.identity, resultCount=self.state.result_count)
                return
            output = await self.wait(lambda o: o.valid, "ResultsValid")
        inserted = self.state.record_result(trigger, output)
        self.event("resultRead", **output.result(), firstRead=inserted, resultCount=self.state.result_count, recovered=recovering)
        await self.inject(trigger, output)
        current = await self.wait(lambda o: o.valid, "fresh held result before ACK")
        self.state.confirm_history(trigger, current)
        # ACK identity always refers to the held result, including after a Busy probe.
        self.write_trigger_data(trigger)
        self.device.setValues(16, 37, [1])
        self.event("resultsAck", identity=trigger.identity)
        cleared = await self.wait(lambda o: not o.valid, "ResultsValid falling edge")
        self.device.setValues(16, 37, [0])
        # Allow the client to observe the low phase before this bounded server exits.
        # Business Ready may stay false after the final product or operator logout.
        await self.wait(lambda o: not o.valid and not o.trigger_ack and o.heartbeat != cleared.heartbeat,
                        "output update after ACK falling edge")
        self.state.finish(trigger)
        self.event("completed", identity=trigger.identity, resultCount=self.state.result_count)

    async def run(self):
        try:
            while self.state.pending is not None or self.state.result_count < self.count:
                recovering = self.state.pending is not None
                if not recovering:
                    # An unknown held result cannot be cleared using a newly invented identity.
                    output = await self.wait(lambda o: o.valid or (o.ready and not o.trigger_ack), "fresh Ready")
                    if output.valid:
                        raise ValueError("Station holds an unknown result; manual recovery required")
                    trigger = self.state.begin(self.product_prefix, self.samples[self.state.result_count % len(self.samples)])
                else:
                    trigger = self.state.pending
                    self.event("recovering", **asdict(trigger))
                await self.complete(trigger, recovering)
            self.event("finished", resultCount=self.state.result_count)
        except Exception as error:
            self.event("error", error=f"{type(error).__name__}: {error}",
                       pending=asdict(self.state.pending) if self.state.pending else None)
            raise


async def serve(simulator, host, port):
    server = ModbusTcpServer(ModbusServerContext(devices={1: simulator.device}, single=False), address=(host, port))
    await server.serve_forever(background=True)
    simulator.event("listening", host=host, port=port, unitId=1, scenario=simulator.scenario)
    try:
        await simulator.run()
    finally:
        await server.shutdown()
