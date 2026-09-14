"""Keep the real TCP simulator running while its station OS process is killed."""
import asyncio
import os
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[2]))
from tools.simulator import __main__ as cli
from tools.simulator.controller import Simulator

folder = Path(os.environ["BOARDTRACE_PLC_PROBE_FOLDER"])
fault = os.environ["BOARDTRACE_PLC_PROBE_FAULT"]


class FinishPaused(Exception):
    pass


async def wait_for_restart():
    async with asyncio.timeout(40):
        while not (folder / "continue-handshake").exists():
            await asyncio.sleep(0.02)


class RecoverySimulator(Simulator):
    async def exchange_trigger(self, trigger, dispositions):
        response = await super().exchange_trigger(trigger, dispositions)
        if response.disposition == 1:
            (folder / "initial-trigger-accepted").touch()
        return response

    async def inject(self, trigger, original):
        if fault == "kill-completed":
            self.event("stationKillBoundary", boundary="completed-before-ack", **original.result())
            (folder / "python-before-ack").touch()
            await wait_for_restart()
        await super().inject(trigger, original)

    async def complete(self, trigger, recovering):
        if fault != "kill-ack":
            await super().complete(trigger, recovering)
            return
        finish = self.state.finish

        def pause_finish(pending):
            self.event("stationKillBoundary", boundary="ack-before-pending-finish", identity=pending.identity)
            (folder / "python-after-ack").touch()
            raise FinishPaused()

        self.state.finish = pause_finish
        try:
            await super().complete(trigger, recovering)
        except FinishPaused:
            pass
        finally:
            self.state.finish = finish
        await wait_for_restart()
        self.event("stationRestarted", pendingIdentity=trigger.identity)
        # The actual product path queries the old identity without new-product Ready,
        # verifies its original durable ledger and only then finishes Pending.
        await super().complete(trigger, True)


if fault not in ("kill-started", "kill-completed", "kill-ack"):
    raise ValueError(f"Unknown process recovery boundary: {fault}")
cli.Simulator = RecoverySimulator
cli.main()
