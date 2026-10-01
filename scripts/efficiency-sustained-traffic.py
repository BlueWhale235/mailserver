#!/usr/bin/env python3
"""Steady 1/10 MiB SMTP acceptance traffic against an isolated Linux instance."""
import argparse
import concurrent.futures
import json
import math
import smtplib
import ssl
import time
import uuid
from pathlib import Path
from urllib.request import urlopen


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--smtp-port", type=int, default=25280)
    parser.add_argument("--health-port", type=int, default=25080)
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--health-url")
    parser.add_argument("--ca-cert", help="Certificate authority file for the health endpoint")
    parser.add_argument("--recipient", default="inbox@efficiency.invalid")
    parser.add_argument("--duration", type=int, default=1800)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    started = time.monotonic()
    latencies, errors, throttles = [], [], []
    sent_bytes = 0
    round_number = 0
    bodies = {size: (b"x" * 76 + b"\r\n") * (size * 1024 * 1024 // 78) for size in (1, 10)}
    health_url = args.health_url or f"http://{args.host}:{args.health_port}/api/v1/health/ready"
    health_tls = ssl.create_default_context(cafile=args.ca_cert)

    def send(size):
        headers = (f"Message-ID: <{uuid.uuid4()}@efficiency.invalid>\r\n"
                   f"From: sender@outside.invalid\r\nTo: {args.recipient}\r\n"
                   "Subject: efficiency endurance\r\n\r\n").encode()
        begin = time.monotonic()
        with smtplib.SMTP(args.host, args.smtp_port, timeout=90) as client:
            client.sendmail("sender@outside.invalid", args.recipient, headers + bodies[size])
        return time.monotonic() - begin

    with args.output.open("w", encoding="utf-8") as output:
        with concurrent.futures.ThreadPoolExecutor(max_workers=3) as executor:
            while time.monotonic() - started < args.duration:
                cycle_start = time.monotonic()
                concurrency = 2 if round_number % 2 == 0 else 3
                size = 1 if round_number % 4 < 2 else 10
                successful = []
                for future in concurrent.futures.as_completed([executor.submit(send, size) for _ in range(concurrency)]):
                    try:
                        elapsed = future.result()
                        successful.append(elapsed)
                        latencies.append(elapsed)
                        sent_bytes += len(bodies[size])
                    except smtplib.SMTPDataError as error:
                        detail = {"code": error.smtp_code, "response": error.smtp_error.decode(errors="replace")}
                        if error.smtp_code == 451 and b"4.3.2 Server busy" in error.smtp_error:
                            throttles.append(detail)
                        else:
                            errors.append(detail)
                    except Exception as error:
                        errors.append(type(error).__name__)
                healthy = False
                try:
                    with urlopen(health_url, timeout=5, context=health_tls) as response:
                        healthy = response.status == 200
                except Exception as error:
                    errors.append("health:" + type(error).__name__)
                output.write(json.dumps({"round": round_number, "elapsedSeconds": time.monotonic() - started,
                    "sizeMiB": size, "concurrency": concurrency, "successes": len(successful),
                    "latencies": successful, "healthy": healthy}) + "\n")
                output.flush()
                round_number += 1
                if not healthy:
                    break
                time.sleep(max(0, min(10 - (time.monotonic() - cycle_start), args.duration - (time.monotonic() - started))))
        ordered = sorted(latencies)
        summary = {"durationSeconds": time.monotonic() - started, "sent": len(latencies), "sentBytes": sent_bytes,
            "errors": errors, "throttles": throttles,
            "p95Seconds": ordered[math.ceil(len(ordered) * .95) - 1] if ordered else None}
        output.write(json.dumps({"summary": summary}) + "\n")
        print(json.dumps(summary))
        if errors or summary["durationSeconds"] < args.duration:
            raise SystemExit(1)


if __name__ == "__main__":
    main()
