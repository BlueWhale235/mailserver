#!/usr/bin/env python3
"""Offline analysis of owned burst evidence; never opens credentials or changes a service."""
import argparse, collections, datetime, json, math, os, statistics
from pathlib import Path

MIB = 1024 ** 2

def read_json(path, default=None):
    try:
        return json.loads(path.read_text())
    except (OSError, ValueError):
        return default

def lines(path):
    if not path.exists():
        return []
    result = []
    for line in path.read_text().splitlines():
        try:
            result.append(json.loads(line[line.index("{"):]))
        except (ValueError, TypeError):
            continue
    return result

def epoch(value):
    if isinstance(value, (int, float)):
        return float(value)
    try:
        return datetime.datetime.fromisoformat(value.replace("Z", "+00:00")).timestamp()
    except (ValueError, TypeError, AttributeError):
        return None

def percentile(values, fraction):
    values = sorted(v for v in values if isinstance(v, (int, float)) and math.isfinite(v))
    return values[max(0, math.ceil(len(values) * fraction) - 1)] if values else None

def distribution(values):
    return {"count":len(values), "p50Seconds":percentile(values,.5), "p95Seconds":percentile(values,.95)}

def milestones(times, t0):
    times = sorted(t for t in times if isinstance(t, (int,float)) and t >= t0)
    return {str(n):times[n-1]-t0 if len(times)>=n else None for n in (500,900,1000)}

def round_summary(r):
    if "skipped" in r:
        return {"round":r["round"],"skipped":r["skipped"],"capacityPassed":False}
    t0 = r["t0"]
    accepted = [e for e in r.get("incoming",[]) if e.get("ok")]
    sink = r.get("received",[])
    incoming_times = [e["completedAt"] for e in accepted]
    outgoing_times = [e["ackAt"] for e in sink if e.get("ackAt") is not None]
    def unique_times(records,timekey):
        bymid={}
        for e in records:
            if e.get(timekey) is not None:bymid[e["mid"]]=min(bymid.get(e["mid"],float("inf")),e[timekey])
        return list(bymid.values())
    incoming_unique_times=unique_times(accepted,"completedAt")
    outgoing_unique_times=unique_times(sink,"ackAt")
    attempts = [a for e in r.get("incoming",[]) for a in e.get("attempts",[])]
    codes = collections.Counter(str(c) for a in attempts for c in
        (a.get("smtpCodes",[]) if "smtpCodes" in a else [a["smtpCode"]] if a.get("smtpCode") is not None else []))
    outgoing_unique30 = len({e["mid"] for e in sink if t0 <= e.get("ackAt",1e99) <= t0+30})
    incoming_unique30 = len({e["mid"] for e in accepted if t0 <= e["completedAt"] <= t0+30})
    ins_expected = 0 if r["round"] == "outgoing" else 1000
    outs_expected = 0 if r["round"] == "incoming" else 1000
    connections = r.get("sinkConnections")
    out = {"round":r["round"],"mode":r.get("mode"),"t0":t0,"elapsedSeconds":r.get("elapsed"),
        "drained":r.get("drained"),"incoming":{
            "expected":ins_expected,"completed30":r.get("incoming30"),"uniqueCompleted30":incoming_unique30,
            "percent30":incoming_unique30 / ins_expected * 100 if ins_expected else None,
            "accepted":len(accepted),"persistedUnique":r.get("incomingPersisted"),
            "duplicateReferences":r.get("incomingDuplicateReferences"),"contentFailures":len(r.get("incomingContentFailures",[])),
            "failedOrNotStarted":sum(not e.get("ok") for e in r.get("incoming",[])),
            "notStarted":sum(bool(e.get("notStarted")) for e in r.get("incoming",[])),
            "recordsMissing":max(0,ins_expected-len(r.get("incoming",[]))),
            "totalAttemptErrors":len(attempts),"rejectedAttempts":sum(count for code,count in codes.items() if int(code)>=400),"smtpCodes":dict(codes),
            "unknownDeliveryOutcomes":sum(bool(a.get("unknownDeliveryOutcome")) for a in attempts),
            "completionMilestonesSeconds":milestones(incoming_unique_times,t0),
            "acceptedOperationLatency":distribution([e["elapsed"] for e in accepted]),
            "acceptedCompletionFromT0":distribution([t-t0 for t in incoming_times])},
        "outgoing":{"expected":outs_expected,"completed30":r.get("outgoing30"),"uniqueCompleted30":outgoing_unique30,
            "percent30":outgoing_unique30/outs_expected*100 if outs_expected else None,
            "received":len(sink),"unique":r.get("outgoingUnique"),"duplicates":r.get("duplicates"),
            "contentFailures":len(r.get("contentFailures",[])),"queue":r.get("queue",{}),
            "queueDelivered":r.get("queue",{}).get("Delivered",0),"queueFailed":r.get("queue",{}).get("Failed",0),
            "queueDeliveredMinusUniqueSinkAck":r.get("queue",{}).get("Delivered",0)-len({e["mid"] for e in sink if "ackAt" in e}),
            "completionMilestonesSeconds":milestones(outgoing_unique_times,t0),
            "acknowledgedBeforeT0":sum(t<t0 for t in outgoing_times),
            "completionFromT0":distribution([t-t0 for t in outgoing_times]),
            "connections":connections,"tlsHandshakes":r.get("sinkTlsHandshakes"),
            "messagesPerConnection":len(sink)/connections if connections else None,
            "targetCompletionRatePerSecond":1000/(sorted(outgoing_unique_times)[999]-t0) if len(outgoing_unique_times)>=1000 and sorted(outgoing_unique_times)[999]>t0 else None},
        "peakQueueProcessing":max((c.get("queue",{}).get("Processing",0) for c in r.get("curve",[])),default=0),
        "peakQueuePendingDeferred":max((c.get("queue",{}).get("Pending",0)+c.get("queue",{}).get("Deferred",0) for c in r.get("curve",[])),default=0)}
    out["capacityPassed"] = (ins_expected==1000 and outs_expected==1000 and incoming_unique30==1000
        and outgoing_unique30==1000 and r.get("incomingPersisted")==1000
        and r.get("outgoingUnique")==1000 and r.get("duplicates")==0
        and r.get("incomingDuplicateReferences")==0 and not r.get("incomingContentFailures")
        and not r.get("contentFailures") and r.get("queue",{}).get("Delivered")==1000
        and out["incoming"]["failedOrNotStarted"]==0 and out["incoming"]["recordsMissing"]==0
        and out["incoming"]["unknownDeliveryOutcomes"]==0 and out["outgoing"]["acknowledgedBeforeT0"]==0)
    return out

def keyvalues(text):
    result={}
    for line in (text or "").splitlines():
        parts=line.replace(":"," ").split()
        if len(parts)>=2:
            try:result[parts[0]]=int(parts[1])
            except ValueError:pass
    return result

def proc_ticks(stat):
    try:
        tail=stat[stat.rindex(")")+2:].split()
        return int(tail[11])+int(tail[12])
    except (ValueError,TypeError,AttributeError,IndexError):
        return None

def host_ticks(stat):
    for line in (stat or "").splitlines():
        if line.startswith("cpu "):
            numbers=list(map(int,line.split()[1:9]))
            return sum(numbers),numbers[3]+numbers[4]
    return None

def resource_summary(samples,t0,end,cores,hz):
    samples=[s for s in samples if t0 <= s.get("timestamp",0) <= end]
    peaks=collections.defaultdict(list)
    cpu=collections.defaultdict(list)
    previous=None
    for s in samples:
        cg=s.get("cgroup",{})
        status=keyvalues(cg.get("memory.stat"))
        for label,v in (("cgroupTotalMiB",cg.get("memory.current")),("cgroupAnonMiB",status.get("anon")),("cgroupFileMiB",status.get("file"))):
            try:peaks[label].append(int(v)/MIB)
            except (ValueError,TypeError):pass
        for role,p in s.get("processes",{}).items():
            rss=keyvalues(p.get("status")).get("VmRSS")
            if rss is not None:peaks[role+"RssMiB"].append(rss/1024)
        if previous:
            dt=s["timestamp"]-previous["timestamp"]
            if dt>0:
                for role,p in s.get("processes",{}).items():
                    old=previous.get("processes",{}).get(role,{})
                    a,b=proc_ticks(p.get("stat")),proc_ticks(old.get("stat"))
                    if p.get("pid")==old.get("pid") and a is not None and b is not None and a>=b:
                        used=(a-b)/hz/dt*100
                        cpu[role+"CpuOneCorePercent"].append((used,dt))
                        if role=="app":cpu["appCpuEffectiveQuotaPercent"].append((used/cores,dt))
                a,b=host_ticks(s.get("host",{}).get("stat")),host_ticks(previous.get("host",{}).get("stat"))
                if a and b and a[0]>b[0]:cpu["hostBusyPercent"].append((100*(1-(a[1]-b[1])/(a[0]-b[0])),dt))
                a,b=keyvalues(cg.get("cpu.stat")),keyvalues(previous.get("cgroup",{}).get("cpu.stat"))
                if "usage_usec" in a and "usage_usec" in b and a["usage_usec"]>=b["usage_usec"]:
                    cpu["cgroupCpuEffectiveQuotaPercent"].append(((a["usage_usec"]-b["usage_usec"])/1e6/dt/cores*100,dt))
        previous=s
    return {"sampleCount":len(samples),"memoryPeaks":{k:max(v) for k,v in peaks.items()},
        "cpu":{k:{"peakPercent":max(v for v,_ in rows),"weightedMeanPercent":sum(v*d for v,d in rows)/sum(d for _,d in rows)} for k,rows in cpu.items()},
        "notes":["RSS, cgroup anon and file overlap: never add them.","CPU quota percent normalized by specified effective cores; process CPU one-core percent can exceed 100.","1 Hz sampling can miss short memory and CPU peaks."]}

def adaptive_summary(samples,t0,end):
    selected=[]
    for row in samples:
        sample=row.get("Sample",row.get("sample",{}))
        at=epoch(sample.get("At",sample.get("at")))
        if at is not None and t0<=at<=end:selected.append(row)
    reasons=collections.Counter()
    maxima=collections.defaultdict(list)
    changes=[]
    previous=None
    for row in selected:
        decision=row.get("Decision",row.get("decision",{}))
        limits={k:decision.get(k,decision.get(k.lower())) for k in ("Inbound","Outbound","Heavy")}
        reasons[decision.get("Reason",decision.get("reason","unknown"))]+=1
        for k,v in limits.items():
            if isinstance(v,(int,float)):maxima[k].append(v)
        for k in ("InboundActive","OutboundActive","HeavyActive","ReservedMemoryBytes"):
            value=row.get(k,row.get(k[0].lower()+k[1:]))
            if isinstance(value,(int,float)):maxima[k].append(value)
        if previous is not None and limits!=previous:
            changes.append({"at":row.get("Sample",{}).get("At"),"limits":limits,"reason":decision.get("Reason")})
        previous=limits
    return {"sampleCount":len(selected),"reasonSampleCounts":dict(reasons),"maximum":{k:max(v) for k,v in maxima.items()},"limitChanges":changes}

def analyze(root,cores=4,hz=100):
    suite=read_json(root/"suite.json",{}) or {}
    rounds={}
    for r in suite.get("results",[]):
        if r.get("round"):rounds[r["round"]]=r
    for path in root.glob("*.json"):
        if path.name in ("private.json","manifest.json"):continue
        if path.stem.startswith(("fixed-","dynamic-","repeat-")) or path.stem in ("incoming","outgoing","diagnostic"):
            r=read_json(path,{})
            if r.get("round") and r.get("t0") is not None:rounds[r["round"]]=r
    resources=lines(root/"resources.jsonl")
    adaptive=lines(root/"adaptive-samples.jsonl")
    if not adaptive:adaptive=lines(root/"adaptive-samples.log")
    summaries=[]
    for label,r in rounds.items():
        summary=round_summary(r)
        if "t0" in r:
            end=r["t0"]+r.get("elapsed",90)
            summary["resources"]=resource_summary(resources,r["t0"],end,cores,hz)
            summary["adaptive"]=adaptive_summary(adaptive,r["t0"],end)
        summary["category"]="formal-comparison" if label in {f"{m}-{n}" for m in ("fixed","dynamic") for n in (1,2,3)} else "continuous-burst" if label.startswith("repeat-") else "diagnostic" if label=="diagnostic" else "single-direction"
        summaries.append(summary)
    comparison={}
    for mode in ("fixed","dynamic"):
        rs=[r for r in summaries if r.get("category")=="formal-comparison" and r.get("mode")==mode]
        comparison[mode]={"expectedRounds":3,"observedRounds":len(rs),"capacityPassed":len(rs)==3 and all(r["capacityPassed"] for r in rs),
            "incoming30":[r.get("incoming",{}).get("uniqueCompleted30") for r in rs],
            "outgoing30":[r.get("outgoing",{}).get("uniqueCompleted30") for r in rs],
            "medianIncomingOperationP95Seconds":percentile([r.get("incoming",{}).get("acceptedOperationLatency",{}).get("p95Seconds") for r in rs],.5),
            "medianOutgoingCompletionP95Seconds":percentile([r.get("outgoing",{}).get("completionFromT0",{}).get("p95Seconds") for r in rs],.5)}
    regressions={}
    for key in ("medianIncomingOperationP95Seconds","medianOutgoingCompletionP95Seconds"):
        a,b=comparison["fixed"].get(key),comparison["dynamic"].get(key)
        regressions[key+"ChangePercent"]=(b/a-1)*100 if a and b is not None else None
    pairs=[]
    for n in (1,2,3):
        pair={"roundNumber":n}
        a=next((r for r in summaries if r["round"]=="fixed-"+str(n)),None)
        b=next((r for r in summaries if r["round"]=="dynamic-"+str(n)),None)
        if a and b and "incoming" in a and "incoming" in b:
            for role,path in (("incoming","acceptedOperationLatency"),("outgoing","completionFromT0")):
                x,y=a[role][path]["p95Seconds"],b[role][path]["p95Seconds"]
                pair[role+"P95ChangePercent"]=(y/x-1)*100 if x and y is not None else None
        pairs.append(pair)
    regressions["pairedP95Changes"]=pairs
    for role in ("incoming","outgoing"):
        changes=[p[role+"P95ChangePercent"] for p in pairs if p.get(role+"P95ChangePercent") is not None]
        regressions[role+"P95MoreThanFivePercentRegressionAllThree"]=len(changes)==3 and all(v>5 for v in changes)
    safety=read_json(root/"safety-stop.json")
    if safety:
        for mode in comparison:comparison[mode]["capacityPassed"]=False
    return {"rounds":summaries,"formalComparison":comparison,"aggregateRegressionIndicators":regressions,
        "suite":{k:v for k,v in suite.items() if k!="results"},"safetyStop":safety,
        "preflight":read_json(root/"preflight.json"),"runtimeBug":read_json(root/"preflight-existing-runtime-bug.json"),
        "measurementNotes":["Incoming operation latency starts when a generator worker starts the mail, excluding generator queue wait; outgoing completion latency starts at common T0 for prequeued work. These are not interchangeable.",
            "Completion milestones and completion-from-T0 distributions include generator queue wait or delivery queue wait.",
            "Nearest-rank P95. Formal comparison includes only fixed-1..3 and dynamic-1..3; single-direction, repeated and profiled rounds excluded.",
            "Temporary negative acknowledgements are counted as attempts, separately from final failures.",
            "A mode requires three complete rounds and all count/content checks before capacityPassed is true.",
            "Missing evidence remains null or absent; CPU tick rate must match target getconf CLK_TCK."]}

if __name__=="__main__":
    parser=argparse.ArgumentParser()
    parser.add_argument("evidence",type=Path)
    parser.add_argument("--output",type=Path)
    parser.add_argument("--cores",type=float,default=4)
    parser.add_argument("--clock-ticks",type=int,default=100)
    args=parser.parse_args()
    result=analyze(args.evidence,args.cores,args.clock_ticks)
    output=args.output or args.evidence/"analysis-summary.json"
    output.write_text(json.dumps(result,indent=2,ensure_ascii=False))
    print(json.dumps({"output":str(output),"formalComparison":result["formalComparison"]},ensure_ascii=False))
