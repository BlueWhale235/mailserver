#!/usr/bin/env python3
"""Measure SMTP session capacity in stages while checking server health and memory.

Run on a test host. When host is localhost, results include client load on that host.
"""

import argparse
import asyncio
import json
import math
import statistics
import time
from pathlib import Path
from urllib.request import urlopen


def mib_from_status(path, names):
    try:
        lines = Path(path).read_text().splitlines()
    except FileNotFoundError:
        return {}
    return {key: int(value.split()[0]) / 1024 for line in lines
            for key, separator, value in [line.partition(":")]
            if separator and key in names}


def snapshot(server_pid):
    server = mib_from_status(f"/proc/{server_pid}/status", {"VmRSS", "VmSwap"})
    system = mib_from_status("/proc/meminfo", {"MemAvailable", "SwapFree"})
    client = mib_from_status("/proc/self/status", {"VmRSS"})
    return {"server_rss_mib": server.get("VmRSS"),
            "server_swap_mib": server.get("VmSwap"),
            "available_mib": system.get("MemAvailable"),
            "swap_free_mib": system.get("SwapFree"),
            "client_rss_mib": client.get("VmRSS")}


def health_ok(url):
    try:
        with urlopen(url, timeout=5) as response:
            return response.status == 200
    except Exception:
        return False


async def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=25)
    parser.add_argument("--server-pid", required=True, type=int)
    parser.add_argument("--health-url", required=True)
    parser.add_argument("--tiers", default="100,200,400,800,1200")
    parser.add_argument("--hold-seconds", type=float, default=5)
    parser.add_argument("--max-server-swap-mib", type=float, default=128)
    parser.add_argument("--min-available-mib", type=float, default=150)
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    tiers = [int(value) for value in args.tiers.split(",")]
    if not tiers or any(value < 1 for value in tiers) or tiers != sorted(set(tiers)):
        parser.error("tiers must be distinct ascending positive integers")

    def emit(record):
        line = json.dumps(record, ensure_ascii=False)
        print(line, flush=True)
        if args.output:
            with args.output.open("a", encoding="utf-8") as output:
                output.write(line + "\n")

    clients = []
    try:
        for tier in tiers:
            started = time.perf_counter()
            timings = []
            errors = {}
            samples = []
            sem = asyncio.Semaphore(32)

            async def connect():
                async with sem:
                    began = time.perf_counter()
                    writer = None
                    try:
                        reader, writer = await asyncio.wait_for(
                            asyncio.open_connection(args.host, args.port), timeout=10)
                        banner = await asyncio.wait_for(reader.readline(), timeout=10)
                        if not banner.startswith(b"220 "):
                            raise ValueError(f"unexpected banner {banner[:3]!r}")
                        clients.append(writer)
                        timings.append((time.perf_counter() - began) * 1000)
                    except Exception as error:
                        name = type(error).__name__
                        errors[name] = errors.get(name, 0) + 1
                        if writer is not None:
                            writer.close()

            pending = [asyncio.create_task(connect()) for _ in range(tier - len(clients))]
            while pending:
                done, pending = await asyncio.wait(pending, timeout=0.1)
                samples.append(snapshot(args.server_pid))
            until = time.monotonic() + args.hold_seconds
            while time.monotonic() < until:
                samples.append(snapshot(args.server_pid))
                await asyncio.sleep(0.1)
            samples.append(snapshot(args.server_pid))
            healthy = await asyncio.to_thread(health_ok, args.health_url)
            values = sorted(timings)
            record = {
                "tier": tier, "connected": len(clients), "errors": errors,
                "elapsed_seconds": round(time.perf_counter() - started, 2),
                "connect_median_ms": round(statistics.median(values), 2) if values else None,
                "connect_p95_ms": round(values[math.ceil(len(values) * 0.95) - 1], 2) if values else None,
                "peak_server_rss_mib": max((x["server_rss_mib"] or 0 for x in samples), default=0),
                "peak_server_swap_mib": max((x["server_swap_mib"] or 0 for x in samples), default=0),
                "min_available_mib": min((x["available_mib"] or 0 for x in samples), default=0),
                "peak_client_rss_mib": max((x["client_rss_mib"] or 0 for x in samples), default=0),
                "health_ok": healthy,
            }
            emit(record)
            reasons = []
            if len(clients) != tier or errors:
                reasons.append("connection_failure")
            if not healthy or not Path(f"/proc/{args.server_pid}").exists():
                reasons.append("server_unhealthy")
            if record["peak_server_swap_mib"] > args.max_server_swap_mib:
                reasons.append("server_swap_limit")
            if record["min_available_mib"] < args.min_available_mib:
                reasons.append("available_memory_limit")
            if reasons:
                emit({"event": "stop", "tier": tier, "reasons": reasons})
                break
    finally:
        for writer in clients:
            writer.close()
        await asyncio.gather(*(writer.wait_closed() for writer in clients), return_exceptions=True)


if __name__ == "__main__":
    asyncio.run(main())
