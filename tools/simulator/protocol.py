from dataclasses import dataclass
from uuid import UUID


@dataclass(frozen=True)
class Trigger:
    session_id: str
    sequence: int
    product_id: str
    sample_id: str

    @property
    def identity(self):
        return self.session_id, self.sequence


def ascii_registers(value, maximum):
    encoded = value.encode("ascii")
    if not 1 <= len(encoded) <= maximum or b"\0" in encoded:
        raise ValueError(f"ASCII text must have 1..{maximum} bytes without NUL")
    padded = encoded.ljust(maximum, b"\0")
    return [len(encoded)] + [int.from_bytes(padded[i:i + 2], "big") for i in range(0, maximum, 2)]


def encode_input(trigger):
    session = UUID(trigger.session_id)
    if not session.int or not 1 <= trigger.sequence <= 0xffffffff:
        raise ValueError("Trigger requires nonzero UUID and uint32 sequence")
    return ([0] + [int.from_bytes(session.bytes[i:i + 2], "big") for i in range(0, 16, 2)]
            + [trigger.sequence >> 16, trigger.sequence & 0xffff]
            + ascii_registers(trigger.product_id, 32) + ascii_registers(trigger.sample_id, 16) + [0])


def uuid_from_words(words):
    return str(UUID(bytes=b"".join(word.to_bytes(2, "big") for word in words)))


@dataclass(frozen=True)
class Output:
    flags: int
    disposition: int
    response_identity: tuple[str, int]
    result_identity: tuple[str, int]
    result_code: int
    inspection_id: str
    production_sequence: int
    heartbeat: int

    @property
    def ready(self):
        return bool(self.flags & 1)

    @property
    def trigger_ack(self):
        return bool(self.flags & 2)

    @property
    def busy(self):
        return bool(self.flags & 4)

    @property
    def valid(self):
        return bool(self.flags & 8)

    @property
    def fault(self):
        return bool(self.flags & 16)

    def result(self):
        if self.result_code not in (1, 2, 3) or not UUID(self.inspection_id).int:
            raise ValueError("Station did not supply a complete valid result")
        return {"sessionId": self.result_identity[0], "sequence": self.result_identity[1],
                "resultCode": self.result_code, "inspectionId": self.inspection_id,
                "productionSequence": self.production_sequence}


def decode_output(words):
    if len(words) != 34:
        raise ValueError("Station output must be one complete 34-register snapshot")
    return Output(words[0], words[1], (uuid_from_words(words[2:10]), words[10] << 16 | words[11]),
                  (uuid_from_words(words[12:20]), words[20] << 16 | words[21]), words[22],
                  uuid_from_words(words[23:31]), words[31] << 16 | words[32], words[33])


class Heartbeat:
    def __init__(self):
        self.value = None
        self.changed_at = None

    def observe(self, value, now):
        if self.value is not None and value != self.value:
            self.changed_at = now
        self.value = value
        return self.changed_at is not None and now - self.changed_at < 2
