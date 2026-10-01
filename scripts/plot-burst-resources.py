#!/usr/bin/env python3
"""Plot sanitized burst evidence; no remote access or modification of inputs."""
import argparse
import datetime as dt
import json
import math
import re
from pathlib import Path
import matplotlib
matplotlib.use('Agg')
import matplotlib.pyplot as plt

MIB = 1024 ** 2
GIB = 1024 ** 3

def json_lines(path):
    if not path.exists():
        return []
    result = []
    for line in path.read_text(errors='replace').splitlines():
        try:
            result.append(json.loads(line[line.index('{'):]))
        except (ValueError, json.JSONDecodeError):
            continue
    return result

def fields(text):
    result = {}
    for line in (text or '').splitlines():
        parts = line.replace(':', '').split()
        if len(parts) > 1:
            try:
                result[parts[0]] = float(parts[1])
            except ValueError:
                pass
    return result

def number(value):
    try:
        return float(value)
    except (ValueError, TypeError):
        return math.nan

def process_cpu(text):
    # /proc/stat field 2 may contain spaces; split after the final closing parenthesis.
    try:
        tail = text[text.rindex(')') + 2:].split()
        return float(tail[11]) + float(tail[12])
    except (TypeError, ValueError, IndexError, AttributeError):
        return math.nan

def host_cpu(text):
    first = (text or '').splitlines()[0].split()[1:]
    values = [float(v) for v in first[:8]]
    return sum(values), values[3] + (values[4] if len(values) > 4 else 0)

def disks(text):
    result = {}
    for line in (text or '').splitlines():
        p = line.split()
        if len(p) >= 14 and re.fullmatch(r'(sd[a-z]+|vd[a-z]+|nvme\d+n\d+|mmcblk\d+)', p[2]):
            result[p[2]] = (float(p[5]) * 512, float(p[9]) * 512)
    return result

def networks(text):
    result = {}
    for line in (text or '').splitlines():
        if ':' not in line:
            continue
        interface, counters = line.split(':', 1)
        interface = interface.strip()
        p = counters.split()
        if len(p) >= 16 and (interface == 'lo' or interface.startswith(('eth', 'en', 'wl'))):
            result[interface] = (float(p[0]), float(p[8]))
    return result

def delta(current, previous, seconds, divisor=1):
    if seconds <= 0 or not math.isfinite(current) or not math.isfinite(previous) or current < previous:
        return math.nan
    return (current - previous) / seconds / divisor

def parse_resources(rows, cores, ticks):
    parsed = []
    previous = None
    previous_source = None
    for row in sorted(rows, key=lambda r: r['timestamp']):
        host, cg = row.get('host', {}), row.get('cgroup', {})
        mem = fields(host.get('meminfo'))
        memory_stat, cpu_stat = fields(cg.get('memory.stat')), fields(cg.get('cpu.stat'))
        item = dict(time=row['timestamp'], round=row.get('round'), app_cpu=math.nan,
                    memory=number(cg.get('memory.current')) / GIB,
                    anon=memory_stat.get('anon', math.nan) / GIB,
                    file=memory_stat.get('file', math.nan) / GIB,
                    available=mem.get('MemAvailable', math.nan) * 1024 / GIB,
                    host_cpu=math.nan, process_cpu={}, disk={}, net={},
                    usage=cpu_stat.get('usage_usec', math.nan), process={})
        for role, proc in row.get('processes', {}).items():
            item['process'][role] = (proc.get('pid'), process_cpu(proc.get('stat')))
        try:
            item['host_ticks'], item['host_idle'] = host_cpu(host.get('stat'))
        except (IndexError, TypeError, ValueError):
            item['host_ticks'], item['host_idle'] = math.nan, math.nan
        item['disk_bytes'], item['net_bytes'] = disks(host.get('diskstats')), networks(host.get('net/dev'))
        if previous:
            seconds = item['time'] - previous['time']
            # cgroup restart resets CPU counter; missing rates remain gaps, never negative spikes.
            same_app = item['process'].get('app', (None,))[0] == previous['process'].get('app', (None,))[0]
            if same_app:
                item['app_cpu'] = delta(item['usage'], previous['usage'], seconds, cores * 10000)
            total = item['host_ticks'] - previous['host_ticks']
            idle = item['host_idle'] - previous['host_idle']
            if total > 0 and idle >= 0:
                item['host_cpu'] = 100 * (1 - idle / total)
            for role, (pid, cpu) in item['process'].items():
                old_pid, old_cpu = previous['process'].get(role, (None, math.nan))
                item['process_cpu'][role] = delta(cpu, old_cpu, seconds, ticks / 100) if pid == old_pid else math.nan
            for category in ('disk', 'net'):
                for name, counters in item[category + '_bytes'].items():
                    old = previous[category + '_bytes'].get(name, (math.nan, math.nan))
                    item[category][name] = [delta(counters[i], old[i], seconds, MIB) for i in range(2)]
        parsed.append(item)
        previous = item
    return parsed

def timestamp(value):
    if isinstance(value, (int, float)):
        return float(value)
    try:
        return dt.datetime.fromisoformat(value.replace('Z', '+00:00')).timestamp()
    except (AttributeError, ValueError):
        return math.nan

def adaptive_samples(path):
    result = []
    for r in json_lines(path):
        r = r.get('payload', r)
        sample = r.get('Sample', r.get('sample', {}))
        decision = r.get('Decision', r.get('decision', {}))
        at = timestamp(sample.get('At', sample.get('at')))
        if math.isfinite(at):
            result.append((at, sample, decision))
    return result

def plot_line(ax, x, y, label, **kwargs):
    if any(math.isfinite(v) for v in y):
        ax.plot(x, y, label=label, **kwargs)

def render(name, rows, rounds, adaptive, output, safety, all_start, all_end):
    if name == 'full-run':
        start, end = all_start, all_end
        selected_rounds = rounds
    else:
        selected_rounds = [r for r in rounds if r.get('round') == name]
        if not selected_rounds:
            return None
        start = selected_rounds[0]['t0']
        end = start + max(35, selected_rounds[0].get('elapsed', 30)) + 2
    data = [r for r in rows if start - 1 <= r['time'] <= end]
    if not data:
        return None
    scale = 60 if name == 'full-run' else 1
    x = [(r['time'] - start) / scale for r in data]
    fig, axes = plt.subplots(6, 2, figsize=(17, 21), constrained_layout=True)
    ax = axes.ravel()
    plot_line(ax[0], x, [r['app_cpu'] for r in data], 'App cgroup CPU / 4-core quota')
    ax[0].axhline(100, color='red', linestyle='--', alpha=.5)
    ax[0].set_ylabel('Quota utilization (%)')
    for metric, label in [('memory', 'Cgroup current'), ('anon', 'Cgroup anon'), ('file', 'Cgroup file/cache')]:
        plot_line(ax[1], x, [r[metric] for r in data], label)
    ax[1].axhline(4, color='red', linestyle='--', label='App limit: 4 GiB')
    ax[1].set_ylabel('GiB (components are not added to RSS)')
    plot_line(ax[2], x, [r['host_cpu'] for r in data], 'Host CPU (all cores)')
    ax[2].set_ylabel('Host utilization (%)')
    plot_line(ax[3], x, [r['available'] for r in data], 'Host MemAvailable')
    ax[3].set_ylabel('GiB')
    roles = sorted({role for r in data for role in r['process_cpu'] if role != 'app'})
    for role in roles:
        plot_line(ax[4], x, [r['process_cpu'].get(role, math.nan) for r in data], role)
    ax[4].set_ylabel('Process CPU (%; 100 = one core)')
    for device in sorted({k for r in data for k in r['disk']}):
        for i, label in enumerate(('read', 'write')):
            plot_line(ax[5], x, [r['disk'].get(device, (math.nan, math.nan))[i] for r in data], f'{device} {label}')
    ax[5].set_ylabel('Physical disk MiB/s (partitions excluded)')
    for interface in sorted({k for r in data for k in r['net']}):
        for i, label in enumerate(('rx', 'tx')):
            plot_line(ax[6], x, [r['net'].get(interface, (math.nan, math.nan))[i] for r in data], f'{interface} {label}')
    ax[6].set_ylabel('Network MiB/s (lo = local traffic)')
    ax[7].set_ylabel('Completed emails (per round)')
    ax[8].set_ylabel('Outbound queue jobs')
    ax[10].set_ylabel('Completed emails / second')
    ax[11].set_ylabel('Rolling operation / database P95 (ms)')
    for r in selected_rounds:
        curve = r.get('curve', [])
        cx = [(r['t0'] - start + v['elapsed']) / scale for v in curve]
        prefix = r['round'] + ' ' if name == 'full-run' else ''
        ax[7].plot(cx, [v.get('incomingAck', 0) for v in curve], label=prefix + 'inbound ACK')
        ax[7].plot(cx, [v.get('sinkReceived', 0) for v in curve], '--', label=prefix + 'outbound sink received')
        ax[8].plot(cx, [sum(v.get('queue', {}).get(k, 0) for k in ('Pending', 'Processing', 'Deferred')) for v in curve], label=prefix + 'active backlog')
        for metric, label in [('incomingAck', 'inbound ACK/s'), ('sinkReceived', 'outbound received/s')]:
            rates = [delta(curve[i].get(metric, 0), curve[i - 1].get(metric, 0), curve[i]['elapsed'] - curve[i - 1]['elapsed']) for i in range(1, len(curve))]
            ax[10].plot(cx[1:], rates, label=prefix + label)
    ax[7].axhline(1000, color='grey', linestyle=':')
    selected_adaptive = [(t, s, d) for t, s, d in adaptive if start - 1 <= t <= end]
    if selected_adaptive:
        tx = [(t - start) / scale for t, _, _ in selected_adaptive]
        for key in ('Inbound', 'Outbound', 'Heavy'):
            plot_line(ax[9], tx, [number(d.get(key, d.get(key.lower()))) for _, _, d in selected_adaptive], key + ' limit', drawstyle='steps-post')
        for key, label in [('DatabaseP95Milliseconds', 'Database commands'), ('OperationP95Milliseconds', 'Completed operations')]:
            plot_line(ax[11], tx, [number(s.get('Telemetry', s.get('telemetry', {})).get(key)) for _, s, _ in selected_adaptive], label)
        second = ax[9].twinx()
        plot_line(second, tx, [number(s.get('GcPausePercent', s.get('gcPausePercent'))) for _, s, _ in selected_adaptive], 'GC pause', color='purple', linestyle=':')
        second.set_ylabel('GC pause (%)')
        handles, labels = second.get_legend_handles_labels()
        if handles:
            second.legend(handles, labels, loc='upper right', fontsize=8)
    else:
        ax[9].text(.05, .5, 'No adaptive samples available', transform=ax[9].transAxes)
    ax[9].set_ylabel('Configured task limits')
    titles = ['App CPU', 'App memory', 'Host CPU', 'Host available memory', 'MySQL and client/sink CPU',
              'Disk throughput', 'Network throughput', 'Completion curve', 'Queue backlog', 'Limits and GC', 'Per-second completion rate', 'Recent latency']
    for a, title in zip(ax, titles):
        a.set_title(title)
        a.set_xlabel('Minutes from first resource sample' if scale == 60 else 'Seconds from T0')
        a.grid(alpha=.2)
        for r in selected_rounds:
            t0 = (r['t0'] - start) / scale
            a.axvspan(t0, t0 + 30 / scale, color='green', alpha=.07)
            a.axvline(t0 + 30 / scale, color='darkgreen', linestyle=':', alpha=.6)
        if safety and start <= number(safety.get('time')) <= end:
            a.axvline((safety['time'] - start) / scale, color='red', linestyle='--')
        handles, labels = a.get_legend_handles_labels()
        if handles:
            a.legend(fontsize=7, loc='best', ncol=2 if len(labels) > 6 else 1)
    fig.suptitle(f'Burst resource evidence: {name}\nGreen windows = 30-second targets; red marker = safety stop. Host/client/MySQL share hardware but are outside app cgroup.', fontsize=13)
    output.mkdir(parents=True, exist_ok=True)
    paths = []
    for extension in ('png', 'svg'):
        path = output / f'{name}-resources.{extension}'
        fig.savefig(path, dpi=140)
        paths.append(str(path))
    plt.close(fig)
    return paths

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--input', required=True, type=Path)
    parser.add_argument('--output', required=True, type=Path)
    parser.add_argument('--cpu-quota', default=4, type=float)
    parser.add_argument('--clock-ticks', default=100, type=float)
    args = parser.parse_args()
    rows = json_lines(args.input / 'resources.jsonl')
    if not rows:
        raise SystemExit('No valid resource rows; refusing to generate empty evidence plots.')
    parsed = parse_resources(rows, args.cpu_quota, args.clock_ticks)
    suite_path = args.input / 'suite.json'
    rounds = json.loads(suite_path.read_text()).get('results', []) if suite_path.exists() else []
    for pattern in ('fixed-*.json', 'dynamic-*.json', 'incoming.json', 'outgoing.json', 'diagnostic.json'):
        for path in args.input.glob(pattern):
            r = json.loads(path.read_text())
            if r.get('t0') and all(r.get('round') != old.get('round') for old in rounds):
                rounds.append(r)
    rounds = [r for r in rounds if r.get('t0')]
    adaptive = adaptive_samples(args.input / 'adaptive-samples.jsonl')
    stop_path = args.input / 'safety-stop.json'
    safety = json.loads(stop_path.read_text()) if stop_path.exists() else None
    result = {'plots': [], 'resourceSamples': len(parsed), 'adaptiveSamples': len(adaptive),
              'cpuQuotaCores': args.cpu_quota, 'clockTicksPerSecond': args.clock_ticks,
              'notes': ['Cgroup anon/file are components; RSS is not added.',
                        'Host disk/network may include unrelated services.',
                        'Loopback rates are local transfers, not physical NIC utilization.',
                        'Process CPU uses 100 percent per core; app CPU uses the 4-core quota.',
                        'GC pause and limits are absent unless adaptive samples were exported.']}
    for name in ('full-run', 'fixed-1', 'dynamic-1'):
        paths = render(name, parsed, rounds, adaptive, args.output, safety, parsed[0]['time'], parsed[-1]['time'])
        if paths:
            result['plots'].extend(paths)
    args.output.mkdir(parents=True, exist_ok=True)
    (args.output / 'plot-manifest.json').write_text(json.dumps(result, indent=2))
    print(json.dumps(result, indent=2))

if __name__ == '__main__':
    main()
