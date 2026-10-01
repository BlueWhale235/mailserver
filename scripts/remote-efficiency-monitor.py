#!/usr/bin/env python3
"""Read-only Linux process and host samples for a remote efficiency test."""
import argparse,json,os,time
from pathlib import Path

parser=argparse.ArgumentParser()
parser.add_argument('--pid',type=int,required=True)
parser.add_argument('--duration',type=int,default=2100)
parser.add_argument('--output',type=Path,required=True)
parser.add_argument('--additional-pid',type=int,default=0)
parser.add_argument('--client-pid',type=int,default=0)
args=parser.parse_args()
started=time.monotonic()
previous_cpu=None
previous_time=None
with args.output.open('w') as output:
    while time.monotonic()-started<args.duration:
        record={'time':time.time(),'elapsedSeconds':time.monotonic()-started,'alive':Path(f'/proc/{args.pid}').exists()}
        if not record['alive']:
            output.write(json.dumps(record)+'\n'); output.flush(); break
        for path in (Path(f'/proc/{args.pid}/status'),Path('/proc/meminfo')):
            for line in path.read_text().splitlines():
                key,_,value=line.partition(':')
                if key in ('VmRSS','VmHWM','VmSwap','MemTotal','MemAvailable','SwapFree','SwapTotal'):
                    record[key+'Bytes']=int(value.split()[0])*1024
                elif key=='Threads':record['threads']=int(value.strip())
        # Split after the comm field, which can itself contain spaces.
        fields=Path(f'/proc/{args.pid}/stat').read_text().rsplit(')',1)[1].split()
        cpu=(int(fields[11])+int(fields[12]))/os.sysconf('SC_CLK_TCK')
        current=time.monotonic()
        record['cpuSeconds']=cpu
        if previous_cpu is not None:
            record['cpuPercentOneCore']=100*(cpu-previous_cpu)/(current-previous_time)
        previous_cpu,previous_time=cpu,current
        for line in Path(f'/proc/{args.pid}/io').read_text().splitlines():
            key,_,value=line.partition(':')
            if key in ('read_bytes','write_bytes'):record[key]=int(value.strip())
        record['loadAverage']=list(os.getloadavg())
        record['hostCpuTicks']=[int(value) for value in Path('/proc/stat').read_text().splitlines()[0].split()[1:]]
        record['hostVmstat']={key:int(value) for key,value in (line.split() for line in Path('/proc/vmstat').read_text().splitlines()) if key in ('pswpin','pswpout','pgmajfault')}
        record['diskStats']={fields[2]:[int(value) for value in fields[3:]] for fields in (line.split() for line in Path('/proc/diskstats').read_text().splitlines()) if not fields[2].startswith(('loop','ram'))}
        record['pressure']={kind:Path('/proc/pressure/'+kind).read_text().strip() for kind in ('cpu','memory','io') if Path('/proc/pressure/'+kind).exists()}
        cgroup=next((line.split(':',2)[2] for line in Path(f'/proc/{args.pid}/cgroup').read_text().splitlines() if line.startswith('0::')),None)
        if cgroup is not None:
            directory=Path('/sys/fs/cgroup')/cgroup.lstrip('/')
            record['cgroup']={name:(directory/name).read_text().strip() for name in ('memory.current','memory.peak','memory.max','memory.events','memory.stat','cpu.stat','io.stat') if (directory/name).exists()}
        if args.additional_pid and Path(f'/proc/{args.additional_pid}').exists():
            additional=Path(f'/proc/{args.additional_pid}')
            fields=additional.joinpath('stat').read_text().rsplit(')',1)[1].split()
            record['additionalProcess']={'pid':args.additional_pid,'cpuSeconds':(int(fields[11])+int(fields[12]))/os.sysconf('SC_CLK_TCK')}
            for line in additional.joinpath('io').read_text().splitlines():
                key,_,value=line.partition(':')
                if key in ('read_bytes','write_bytes'):record['additionalProcess'][key]=int(value.strip())
        if args.client_pid and Path(f'/proc/{args.client_pid}').exists():
            client=Path(f'/proc/{args.client_pid}');fields=client.joinpath('stat').read_text().rsplit(')',1)[1].split()
            record['clientProcess']={'pid':args.client_pid,'cpuSeconds':(int(fields[11])+int(fields[12]))/os.sysconf('SC_CLK_TCK')}
            for line in client.joinpath('status').read_text().splitlines():
                if line.startswith('VmRSS:'):record['clientProcess']['rssBytes']=int(line.split()[1])*1024
            for line in client.joinpath('io').read_text().splitlines():
                key,_,value=line.partition(':')
                if key in ('read_bytes','write_bytes'):record['clientProcess'][key]=int(value.strip())
        output.write(json.dumps(record)+'\n')
        output.flush()
        time.sleep(1)
