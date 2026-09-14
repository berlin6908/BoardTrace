import argparse
import asyncio
import json
from pathlib import Path

from .controller import Simulator, create_device, serve
from .protocol import ascii_registers
from .state import SimulatorState


def read_samples(path):
    samples = []
    for line in path.read_text(encoding="utf-8-sig").splitlines():
        row = json.loads(line)
        if not isinstance(row, dict) or set(row) != {"sampleId"}:
            raise ValueError("Samples JSONL must contain only sampleId in each row")
        ascii_registers(row["sampleId"], 16)
        samples.append(row["sampleId"])
    if not samples or len(set(samples)) != len(samples):
        raise ValueError("Samples must be nonempty and unique")
    return samples


def main():
    parser = argparse.ArgumentParser(description="PLC products and trigger events; station supplies every quality result")
    parser.add_argument("--scenario", choices=("normal", "duplicate-trigger", "busy", "lost-ack"), default="normal")
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=1502)
    parser.add_argument("--count", type=int, default=1, help="Target total unique results in this SQLite state, including prior runs")
    parser.add_argument("--product-prefix", required=True,
                        help="1..21 ASCII characters; new product IDs are prefix-TriggerSequence")
    parser.add_argument("--samples", type=Path, required=True, help="JSONL containing only sampleId")
    parser.add_argument("--state", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--timeout", type=float, default=30, help="Seconds allowed for each handshake wait")
    parser.add_argument("--ack-delay", type=float, default=3, help="Seconds to omit ACK once in lost-ack scenario")
    args = parser.parse_args()
    if args.count < 1 or args.timeout <= 0 or args.ack_delay < 0 or not 1 <= args.port <= 65535:
        parser.error("count/timeout must be positive, ack-delay nonnegative, port 1..65535")
    ascii_registers(args.product_prefix, 21)
    samples = read_samples(args.samples)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    with SimulatorState(args.state) as state, args.output.open("a", encoding="utf-8") as output:
        simulator = Simulator(create_device(), state, samples, args.product_prefix, output,
                              scenario=args.scenario, count=args.count, timeout=args.timeout, ack_delay=args.ack_delay)
        asyncio.run(serve(simulator, args.host, args.port))


if __name__ == "__main__":
    main()
