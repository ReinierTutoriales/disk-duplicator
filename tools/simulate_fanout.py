"""Deterministic, block-level model of the current fan-out path and an optimistic spool.

Units are MiB and seconds. Rates are *effective measured* rates, not device labels.
No caching, verification, controller sharing, filesystem overhead or queue-depth gain is
assumed. The optional spool is a separate idealized device; replay reads are free,
so its result is an upper bound, not a proposed production implementation.
"""
from __future__ import annotations

import argparse
from collections import deque
from dataclasses import dataclass, field
import heapq
import json
from typing import Deque


@dataclass
class Block:
    size: float
    references: int


@dataclass
class Target:
    name: str
    rate: float
    stall_start: float = -1.0
    stall_end: float = -1.0
    latency_ms: float = 0.0
    queue: Deque[Block] = field(default_factory=deque)
    written: float = 0.0
    busy: bool = False
    finished_at: float | None = None


def service_end(start: float, amount: float, rate: float, stall_start: float = -1.0,
                stall_end: float = -1.0) -> float:
    if rate <= 0:
        raise ValueError("Las tasas deben ser positivas")
    duration = amount / rate
    if stall_end <= stall_start or start >= stall_end:
        return start + duration
    if start >= stall_start:
        return stall_end + duration
    if start + duration > stall_start:
        return stall_end + duration - (stall_start - start)
    return start + duration


def simulate(size: float, source_rate: float, targets: list[Target], pool: float = 256,
             block_size: float = 8, spool_target: str | None = None,
             spool_rate: float = 1200, spool_capacity: float = 0,
             source_latency_ms: float = 0) -> dict:
    if size <= 0 or pool < block_size or block_size <= 0 or source_rate <= 0 or source_latency_ms < 0:
        raise ValueError("Tamaño, origen, bloque y pool deben ser positivos y compatibles")
    if not targets or len({t.name for t in targets}) != len(targets) or any(t.rate <= 0 or t.latency_ms < 0 for t in targets):
        raise ValueError("Se requieren destinos únicos con tasas positivas")
    if spool_target is not None and (spool_target not in {t.name for t in targets} or
                                     spool_rate <= 0 or spool_capacity < block_size):
        raise ValueError("Spool requiere un destino, tasa y capacidad de al menos un bloque")
    for target in targets:
        target.queue.clear()
        target.written = 0.0
        target.busy = False
        target.finished_at = None
    target_by_name = {t.name: t for t in targets}
    events: list[tuple[float, int, str, object]] = []
    sequence = 0
    clock = 0.0
    read = 0.0
    used = 0.0
    peak_used = 0.0
    source_busy = False
    spool_busy = False
    spool_used = 0.0
    peak_spool = 0.0
    spool_queue: Deque[Block] = deque()
    samples: list[dict] = []

    def schedule(when: float, kind: str, value: object) -> None:
        nonlocal sequence
        sequence += 1
        heapq.heappush(events, (when, sequence, kind, value))

    def start_read() -> None:
        nonlocal source_busy
        amount = min(block_size, size - read)
        if source_busy or amount <= 1e-8 or used + amount > pool + 1e-8:
            return
        source_busy = True
        schedule(clock + amount / source_rate + source_latency_ms / 1000, "read", amount)

    def start_target(t: Target) -> None:
        if t.busy or not t.queue:
            return
        t.busy = True
        amount = t.queue[0].size
        schedule(service_end(clock, amount, t.rate, t.stall_start, t.stall_end) + t.latency_ms / 1000,
                 "write", t.name)

    def start_spool() -> None:
        nonlocal spool_busy, spool_used, peak_spool
        if spool_busy or not spool_queue:
            return
        amount = spool_queue[0].size
        if spool_used + amount > spool_capacity + 1e-8:
            return
        spool_busy = True
        spool_used += amount
        peak_spool = max(peak_spool, spool_used)
        schedule(clock + amount / spool_rate, "spool", None)

    def release(block: Block) -> None:
        nonlocal used
        block.references -= 1
        if block.references == 0:
            used -= block.size
            start_read()

    start_read()
    schedule(1.0, "sample", None)
    while events:
        clock, _, kind, value = heapq.heappop(events)
        if kind == "read":
            source_busy = False
            amount = float(value)
            read += amount
            used += amount
            peak_used = max(peak_used, used)
            block = Block(amount, len(targets))
            for t in targets:
                if t.name == spool_target:
                    spool_queue.append(block)
                else:
                    t.queue.append(block)
                    start_target(t)
            start_spool()
            start_read()
        elif kind == "write":
            t = target_by_name[str(value)]
            block = t.queue.popleft()
            t.busy = False
            t.written += block.size
            if t.name == spool_target:
                spool_used -= block.size
                start_spool()
            else:
                release(block)
            if t.finished_at is None and t.written >= size - 1e-8:
                t.finished_at = clock
            start_target(t)
        elif kind == "spool":
            spool_busy = False
            block = spool_queue.popleft()
            release(block)
            target_by_name[spool_target].queue.append(block)
            start_target(target_by_name[spool_target])
            start_spool()
        elif kind == "sample":
            samples.append({"second": round(clock, 3), "source_mib": round(read, 3),
                            "pool_mib": round(used, 3), "spool_mib": round(spool_used, 3),
                            "written_mib": {t.name: round(t.written, 3) for t in targets},
                            "ui_progress_mib": round(min(t.written for t in targets), 3)})
            if any(t.finished_at is None for t in targets):
                schedule(clock + 1, "sample", None)
            else:
                break
    if any(t.finished_at is None for t in targets):
        raise RuntimeError("La simulación no terminó")
    return {"size_mib": size, "source_rate_mib_s": source_rate,
            "pool_mib": pool, "peak_pool_mib": round(peak_used, 3),
            "spool_capacity_mib": spool_capacity if spool_target else 0,
            "peak_spool_mib": round(peak_spool, 3),
            "destinations": {t.name: {"rate_limit_mib_s": t.rate,
                                       "finished_s": round(t.finished_at, 3),
                                       "average_mib_s": round(size / t.finished_at, 3)} for t in targets},
            "job_finished_s": round(max(t.finished_at for t in targets), 3),
            "samples": samples}


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--size", type=float, default=4096, help="MiB to copy")
    parser.add_argument("--source", type=float, default=1600, help="source MiB/s")
    parser.add_argument("--fast", type=float, default=1000, help="fast destination MiB/s")
    parser.add_argument("--slow", type=float, default=40, help="slow USB MiB/s")
    parser.add_argument("--source-latency-ms", type=float, default=0, help="latency per source read")
    parser.add_argument("--fast-latency-ms", type=float, default=0, help="latency per fast write")
    parser.add_argument("--slow-latency-ms", type=float, default=0, help="latency per USB write")
    parser.add_argument("--pool", type=float, default=256, help="shared pool MiB")
    parser.add_argument("--block", type=float, default=8, help="block MiB")
    parser.add_argument("--stall-start", type=float, default=-1, help="USB stall start in seconds")
    parser.add_argument("--stall-end", type=float, default=-1, help="USB stall end in seconds")
    parser.add_argument("--spool-capacity", type=float, default=0, help="idealized spool MiB, zero disables")
    parser.add_argument("--spool-rate", type=float, default=1200, help="idealized spool write MiB/s")
    args = parser.parse_args()
    result = simulate(args.size, args.source,
                      [Target("NVMe", args.fast, latency_ms=args.fast_latency_ms),
                       Target("USB", args.slow, args.stall_start, args.stall_end, args.slow_latency_ms)],
                      args.pool, args.block, "USB" if args.spool_capacity else None,
                      args.spool_rate, args.spool_capacity, args.source_latency_ms)
    print(json.dumps(result, indent=2))


if __name__ == "__main__":
    main()
