import json
import sqlite3
from dataclasses import asdict
from uuid import uuid4

from .protocol import Trigger, ascii_registers, encode_input


class SimulatorState:
    """Pending trigger commits before its rising edge; results commit before ACK."""

    def __init__(self, path):
        path.parent.mkdir(parents=True, exist_ok=True)
        self.db = sqlite3.connect(path)
        self.db.execute("PRAGMA journal_mode=WAL")
        self.db.execute("PRAGMA synchronous=FULL")
        self.db.executescript("""
            CREATE TABLE IF NOT EXISTS Controller (
                Id INTEGER PRIMARY KEY CHECK(Id=1), SessionId TEXT NOT NULL, NextSequence INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS Pending (
                Id INTEGER PRIMARY KEY CHECK(Id=1), TriggerJson TEXT NOT NULL,
                ProbeSequence INTEGER, InjectionDone INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE IF NOT EXISTS Results (
                SessionId TEXT NOT NULL, Sequence INTEGER NOT NULL, ResultJson TEXT NOT NULL,
                PRIMARY KEY(SessionId, Sequence));
        """)
        self.recovering = self.pending is not None
        if not self.recovering:
            with self.db:
                # A new process has a new session, but never reuses a product sequence in this state.
                self.db.execute("""INSERT INTO Controller VALUES (1, ?, 1)
                    ON CONFLICT(Id) DO UPDATE SET SessionId=excluded.SessionId""", (str(uuid4()),))

    def __enter__(self):
        return self

    def __exit__(self, *_):
        self.db.close()

    @property
    def pending(self):
        row = self.db.execute("SELECT TriggerJson FROM Pending WHERE Id=1").fetchone()
        return Trigger(**json.loads(row[0])) if row else None

    @property
    def result_count(self):
        return self.db.execute("SELECT COUNT(*) FROM Results").fetchone()[0]

    def begin(self, product_prefix, sample_id):
        ascii_registers(product_prefix, 21)
        with self.db:
            if self.pending is not None:
                raise ValueError("Pending trigger must complete before a new product")
            session, sequence = self.db.execute("SELECT SessionId, NextSequence FROM Controller WHERE Id=1").fetchone()
            if sequence > 0xffffffff:
                raise OverflowError("Trigger sequence exhausted; use a new controller state and product prefix")
            trigger = Trigger(session, sequence, f"{product_prefix}-{sequence}", sample_id)
            encode_input(trigger)
            self.db.execute("INSERT INTO Pending(Id, TriggerJson) VALUES (1, ?)", (json.dumps(asdict(trigger)),))
            self.db.execute("UPDATE Controller SET SessionId=?, NextSequence=? WHERE Id=1", (session, sequence + 1))
        return trigger

    def reserve_probe(self, trigger):
        with self.db:
            row = self.db.execute("SELECT ProbeSequence FROM Pending WHERE Id=1").fetchone()
            sequence = row[0]
            if sequence is None:
                sequence = self.db.execute("SELECT NextSequence FROM Controller WHERE Id=1").fetchone()[0]
                if sequence > 0xffffffff:
                    raise OverflowError("Trigger sequence exhausted; cannot reserve a Busy probe")
                self.db.execute("UPDATE Controller SET NextSequence=? WHERE Id=1", (sequence + 1,))
                self.db.execute("UPDATE Pending SET ProbeSequence=? WHERE Id=1", (sequence,))
        product_prefix = trigger.product_id.rsplit("-", 1)[0]
        return Trigger(trigger.session_id, sequence, f"{product_prefix}-{sequence}", trigger.sample_id)

    @property
    def injection_done(self):
        return bool(self.db.execute("SELECT InjectionDone FROM Pending WHERE Id=1").fetchone()[0])

    def mark_injection(self):
        with self.db:
            self.db.execute("UPDATE Pending SET InjectionDone=1 WHERE Id=1")

    def record_result(self, trigger, output):
        if output.result_identity != trigger.identity or self.pending != trigger:
            raise ValueError("Result identity does not match the durable pending trigger")
        if not output.valid:
            raise ValueError("Cannot record a result without ResultsValid")
        result_json = json.dumps(output.result(), sort_keys=True)
        with self.db:
            row = self.db.execute("SELECT ResultJson FROM Results WHERE SessionId=? AND Sequence=?", trigger.identity).fetchone()
            if row:
                if row[0] != result_json:
                    raise ValueError("Station result changed for an existing identity")
                return False
            self.db.execute("INSERT INTO Results VALUES (?, ?, ?)", (*trigger.identity, result_json))
        return True

    def confirm_history(self, trigger, output):
        row = self.db.execute("SELECT ResultJson FROM Results WHERE SessionId=? AND Sequence=?", trigger.identity).fetchone()
        if output.result_identity != trigger.identity or row is None or row[0] != json.dumps(output.result(), sort_keys=True):
            raise ValueError("Historical duplicate does not match the local result ledger")

    def finish(self, trigger):
        with self.db:
            if self.pending != trigger or not self.db.execute(
                    "SELECT 1 FROM Results WHERE SessionId=? AND Sequence=?", trigger.identity).fetchone():
                raise ValueError("Cannot finish a trigger without its durable result")
            self.db.execute("DELETE FROM Pending WHERE Id=1")
            if self.recovering:
                self.db.execute("UPDATE Controller SET SessionId=? WHERE Id=1", (str(uuid4()),))
        self.recovering = False
