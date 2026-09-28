#!/usr/bin/env python3
"""Bounded SMTP/slow-connection load for the isolated 512 MiB acceptance container."""
import argparse
import concurrent.futures
import json
import socket
import smtplib
import time
from email.message import EmailMessage


def send(port, index, attachment_bytes):
    started = time.monotonic()
    message = EmailMessage()
    message["From"] = "sender@outside.test"
    message["To"] = "alice@acceptance.test"
    message["Subject"] = f"acceptance-{index}"
    message.set_content("mailserver acceptance message")
    if index % 10 == 0:
        message.add_attachment(b"A" * attachment_bytes, maintype="application",
                               subtype="octet-stream", filename="attachment.bin")
    with smtplib.SMTP("127.0.0.1", port, timeout=20) as client:
        client.send_message(message)
    return time.monotonic() - started


def slow_client(port):
    try:
        with socket.create_connection(("127.0.0.1", port), timeout=5) as stream:
            stream.settimeout(5)
            stream.recv(4096)
            stream.sendall(b"EHLO slow.example\r\n")
            time.sleep(2)
    except OSError:
        pass


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--smtp-port", type=int, required=True)
    parser.add_argument("--imap-port", type=int, required=True)
    parser.add_argument("--duration", type=int, default=300)
    parser.add_argument("--attachment-bytes", type=int, default=4 * 1024 * 1024)
    args = parser.parse_args()
    latencies = []
    failures = []
    started = time.monotonic()
    index = 0
    last_slow_index = -1
    with concurrent.futures.ThreadPoolExecutor(max_workers=5) as pool:
        pending = set()
        while time.monotonic() - started < args.duration:
            while len(pending) < 4:
                pending.add(pool.submit(send, args.smtp_port, index, args.attachment_bytes))
                index += 1
            done, pending = concurrent.futures.wait(pending, timeout=1,
                                                     return_when=concurrent.futures.FIRST_COMPLETED)
            for future in done:
                try:
                    latencies.append(future.result())
                except Exception as error:
                    failures.append(type(error).__name__ + ": " + str(error))
            if index % 20 == 0 and index != last_slow_index:
                pool.submit(slow_client, args.smtp_port)
                pool.submit(slow_client, args.imap_port)
                last_slow_index = index
        for future in concurrent.futures.as_completed(pending):
            try:
                latencies.append(future.result())
            except Exception as error:
                failures.append(type(error).__name__ + ": " + str(error))
    ordered = sorted(latencies)
    elapsed = time.monotonic() - started
    print(json.dumps({"duration_seconds": round(elapsed, 2), "sent": len(latencies),
                      "failed": len(failures), "throughput_per_second": round(len(latencies) / elapsed, 3),
                      "p95_seconds": ordered[min(len(ordered) - 1, int(len(ordered) * .95))] if ordered else None,
                      "errors": failures[:20], "attachment_bytes": args.attachment_bytes}, indent=2))


if __name__ == "__main__":
    main()
