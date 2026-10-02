"""Moves a PumpGuard config from hand-written GPU ids (/nvml/0, /gpu-nvidia/0) to automatic GPU discovery.

usage: python migrate-config.py <in.json> <out.json>
GPU temperature rules and GPU extras are dropped (discovery recreates them per card, by PCI bus);
fan curves that followed /gpu-nvidia/0 Hot Spot now follow the stable alias /gpu/1/hotspot.
"""
import io
import json
import sys

GPU_PREFIXES = ("/nvml/", "/gpu-nvidia/", "/gpu-amd/", "/gpu-intel/")
ALIASES = {
    "/gpu-nvidia/0/temperature/2": "/gpu/1/hotspot",
    "/gpu-nvidia/0/temperature/0": "/gpu/1/core",
    "/nvml/0/temperature/core": "/gpu/1/core",
    "/nvml/0/temperature/memory": "/gpu/1/memory",
}


def is_gpu(*ids):
    return any(i and i.startswith(GPU_PREFIXES) for i in ids)


src, dst = sys.argv[1], sys.argv[2]
cfg = json.load(io.open(src, encoding="utf-8-sig"))
pg = cfg["PumpGuard"]

hot_spot = next((r for r in pg.get("Temperatures", []) if "Hot Spot" in r.get("Name", "")), None)
pg["Temperatures"] = [r for r in pg.get("Temperatures", []) if not is_gpu(r.get("SensorId"), *r.get("SensorIds", []))]
pg["Extras"] = [e for e in pg.get("Extras", []) if not is_gpu(e.get("SensorId"))]
gpus = pg.setdefault("Gpus", {"Auto": True, "IgnoreNames": []})
if hot_spot:  # keep the limits the owner had chosen
    gpus.update(HotSpotWarnC=hot_spot["WarnC"], HotSpotShutdownC=hot_spot["ShutdownC"], HotSpotCriticalC=hot_spot["CriticalC"])

for ch in cfg.get("FanControl", {}).get("Channels", []):
    ids = [ALIASES.get(i, i) for i in ch.get("SourceSensorIds", [])]
    # Hot Spot disappears when the card runs in TCC mode; the core temperature is always there.
    if "/gpu/1/hotspot" in ids and "/gpu/1/core" not in ids:
        ids.append("/gpu/1/core")
    ch["SourceSensorIds"] = list(dict.fromkeys(ids))

io.open(dst, "w", encoding="utf-8", newline="\n").write(json.dumps(cfg, ensure_ascii=False, indent=2) + "\n")
print("migrated:", dst)
print("  temperatures:", [r["Name"] for r in pg["Temperatures"]])
print("  extras:", [e["Group"] + "/" + e["Name"] for e in pg["Extras"]])
print("  gpus:", gpus)
