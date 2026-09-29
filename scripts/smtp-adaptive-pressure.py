#!/usr/bin/env python3
"""Find a practical SMTP load boundary with progressively larger message batches.

Run from a separate test host when network capacity is part of the measurement.
The default localhost target measures the combined client and server machine.
"""

import argparse
import concurrent.futures
import json
import math
import smtplib
import statistics
import threading
import time
import uuid
from pathlib import Path
from urllib.request import urlopen


def arguments():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=25)
    parser.add_argument("--sender", required=True)
    parser.add_argument("--recipient", required=True)
    parser.add_argument("--size-mib", type=int, default=10)
    parser.add_argument("--max-concurrency", type=int, default=32)
    parser.add_argument("--messages-per-worker", type=int, default=2)
    parser.add_argument("--timeout-seconds", type=int, default=120)
    parser.add_argument("--health-url")
    parser.add_argument("--server-pid", type=int, help="Linux server PID for RSS and swap samples")
    parser.add_argument("--max-process-swap-mib", type=float, default=128)
    parser.add_argument("--min-available-mib", type=float, default=150)
    parser.add_argument("--max-p95-seconds", type=float, default=30)
    parser.add_argument("--min-throughput-ratio", type=float, default=0.8)
    parser.add_argument("--output", type=Path, help="Append JSON records to this file")
    args = parser.parse_args()
    if min(args.size_mib, args.max_concurrency, args.messages_per_worker, args.timeout_seconds) < 1:
        parser.error("size, concurrency, messages per worker, and timeout must be positive")
    return args


def emit(record, output):
    line = json.dumps(record, ensure_ascii=False)
    print(line, flush=True)
    if output:
        with output.open("a", encoding="utf-8") as file:
            file.write(line + "\n")


def memory(pid):
    if not pid:
        return None
    try:
        status = Path(f"/proc/{pid}/status").read_text()
        meminfo = Path("/proc/meminfo").read_text()
    except FileNotFoundError:
        return None
    values = {}
    for line in (status + "\n" + meminfo).splitlines():
        key, _, value = line.partition(":")
        if key in ("VmRSS", "VmSwap", "MemAvailable", "SwapFree"):
            values[key] = int(value.split()[0]) / 1024
    return values


def health_ok(url):
    if not url:
        return True
    try:
        with urlopen(url, timeout=5) as response:
            return response.status == 200
    except Exception:
        return False


def percentile_95(values):
    values = sorted(values)
    return values[math.ceil(len(values) * 0.95) - 1] if values else None


def main():
    args = arguments()
    # CRLF-safe text payload, approximately the requested wire size.
    body = (b"x" * 76 + b"\r\n") * (args.size_mib * 1024 * 1024 // 78)
    stop = threading.Event()
    samples = []

    def monitor():
        while not stop.wait(0.05):
            sample = memory(args.server_pid)
            if sample is not None:
                samples.append(sample)

    if args.server_pid:
        threading.Thread(target=monitor, daemon=True).start()

    def send(_):
        headers = (f"Message-ID: <{uuid.uuid4()}@pressure.invalid>\r\n"
                   f"Subject: smtp-pressure\r\nFrom: {args.sender}\r\n"
                   f"To: {args.recipient}\r\n\r\n").encode()
        started = time.perf_counter()
        try:
            with smtplib.SMTP(args.host, args.port, timeout=args.timeout_seconds) as smtp:
                smtp.sendmail(args.sender, [args.recipient], headers + body)
            status = "ok"
        except smtplib.SMTPResponseException as error:
            status = f"smtp{error.smtp_code}"
        except Exception as error:
            status = type(error).__name__
        return status, (time.perf_counter() - started) * 1000

    def run(concurrency, count):
        sample_start = len(samples)
        started = time.perf_counter()
        with concurrent.futures.ThreadPoolExecutor(max_workers=concurrency) as pool:
            results = list(pool.map(send, range(count)))
        wall = time.perf_counter() - started
        successes = [duration for status, duration in results if status == "ok"]
        observed = samples[sample_start:] or ([memory(args.server_pid)] if args.server_pid else [])
        observed = [sample for sample in observed if sample]
        record = {
            "concurrency": concurrency,
            "attempted": count,
            "accepted": len(successes),
            "errors": {status: sum(item[0] == status for item in results)
                       for status in sorted({item[0] for item in results}) if status != "ok"},
            "wall_seconds": round(wall, 2),
            "accepted_mib_per_second": round(len(successes) * args.size_mib / wall, 2),
            "median_ok_ms": round(statistics.median(successes), 2) if successes else None,
            "p95_ok_ms": round(percentile_95(successes), 2) if successes else None,
            "peak_rss_mib": round(max((x.get("VmRSS", 0) for x in observed), default=0), 1) if observed else None,
            "peak_process_swap_mib": round(max((x.get("VmSwap", 0) for x in observed), default=0), 1) if observed else None,
            "min_available_mib": round(min((x.get("MemAvailable", 0) for x in observed), default=0), 1) if observed else None,
            "health_ok": health_ok(args.health_url),
            "server_alive": not args.server_pid or Path(f"/proc/{args.server_pid}").exists(),
        }
        emit(record, args.output)
        return record

    try:
        emit({"event": "start", "size_mib": args.size_mib, "host": args.host,
              "port": args.port, "server_pid": args.server_pid}, args.output)
        warmup = run(1, 1)
        if warmup["accepted"] != 1 or not warmup["health_ok"]:
            emit({"event": "stop", "reason": "warmup_failed"}, args.output)
            return 1
        previous = None
        concurrency = 1
        while concurrency <= args.max_concurrency:
            result = run(concurrency, max(4, concurrency * args.messages_per_worker))
            reasons = []
            if result["accepted"] != result["attempted"]:
                reasons.append("delivery_failure")
            if not result["health_ok"] or not result["server_alive"]:
                reasons.append("server_unhealthy")
            if result["p95_ok_ms"] is None or result["p95_ok_ms"] > args.max_p95_seconds * 1000:
                reasons.append("p95_limit")
            if result["peak_process_swap_mib"] is not None and result["peak_process_swap_mib"] > args.max_process_swap_mib:
                reasons.append("process_swap_limit")
            if result["min_available_mib"] is not None and result["min_available_mib"] < args.min_available_mib:
                reasons.append("available_memory_limit")
            if previous and result["accepted_mib_per_second"] < previous["accepted_mib_per_second"] * args.min_throughput_ratio:
                reasons.append("throughput_regression")
            if reasons:
                emit({"event": "stop", "failed_tier": concurrency,
                      "last_acceptable_tier": previous["concurrency"] if previous else None,
                      "reasons": reasons}, args.output)
                break
            previous = result
            concurrency *= 2
        else:
            emit({"event": "stop", "reason": "configured_maximum_reached",
                  "last_acceptable_tier": previous["concurrency"] if previous else None}, args.output)
        return 0
    finally:
        stop.set()


if __name__ == "__main__":
    raise SystemExit(main())
