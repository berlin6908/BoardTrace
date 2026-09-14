"""Integration-only timing/process faults around the real simulator handshake."""
import asyncio
import os
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[2]))
from tools.simulator import __main__ as cli
from tools.simulator.controller import Simulator
from tools.simulator.state import SimulatorState


class CaptureBusySimulator(Simulator):
    async def complete(self, trigger, recovering):
        if recovering:
            raise ValueError("Capture Busy probe requires a new pending trigger")
        await self.exchange_trigger(trigger, (1,))
        await self.wait(lambda output: output.busy and not output.valid, "accepted capture still busy")
        probe = self.state.reserve_probe(trigger)
        response = await self.exchange_trigger(probe, (3,))
        if not response.busy or response.valid or response.result_identity != trigger.identity:
            raise ValueError("Busy probe altered the in-flight inspection or exposed an early result")
        self.event("busyBeforeCaptureVerified", identity=trigger.identity, rejectedIdentity=probe.identity,
                   originalInspectionId=response.inspection_id, resultsValid=response.valid)
        self.write_trigger_data(trigger)
        await super().complete(trigger, False)


class CompletionGateSimulator(Simulator):
    async def complete(self, trigger, recovering):
        if recovering:
            raise ValueError("Completion gate requires a new pending trigger")
        await self.exchange_trigger(trigger, (1,))
        gate = Path(os.environ["BOARDTRACE_PLC_PROBE_FOLDER"])
        async with asyncio.timeout(self.timeout):
            while not (gate / "delegate-committed").exists():
                await asyncio.sleep(0.02)
        response = await self.exchange_trigger(trigger, (2,))
        if not response.valid or not response.busy:
            raise ValueError("Duplicate must expose the committed result while its caller still awaits UI work")
        self.event("completedDuplicateWhileDelegateBlocked", **response.result())
        # The product simulator reads and commits the ledger, then ACKs over TCP.
        await super().complete(trigger, False)
        self.event("ackBeforeDelegateReleased", **response.result())
        (gate / "release-delegate").touch()
        cleared = await self.wait(lambda o: not o.busy and not o.valid, "delegate observed without resurrecting ACKed result")
        self.state.confirm_history(trigger, cleared)
        # Observe another heartbeat to detect a late re-publication after task observation.
        stable = await self.wait(lambda o: o.heartbeat != cleared.heartbeat, "next heartbeat after delegate observation")
        if stable.valid or stable.busy:
            raise ValueError("Completed delegate resurrected an already ACKed result")
        self.state.confirm_history(trigger, stable)
        self.event("noResultResurrection", **stable.result(), resultsValid=stable.valid, busy=stable.busy)


def exit_before_pending_finish(self, trigger):
    # Called by the real handshake after ACK/Valid/low phase, before deleting Pending.
    marker = Path(os.environ["BOARDTRACE_PLC_PROBE_FOLDER"]) / "before-pending-finish"
    marker.write_text(str(trigger.identity), encoding="utf-8")
    os._exit(73)


fault = os.environ["BOARDTRACE_PLC_PROBE_FAULT"]
if fault == "busy-during-capture":
    cli.Simulator = CaptureBusySimulator
elif fault == "completion-gate":
    cli.Simulator = CompletionGateSimulator
elif fault == "ack-crash":
    SimulatorState.finish = exit_before_pending_finish
else:
    raise ValueError(f"Unknown integration fault: {fault}")
cli.main()
