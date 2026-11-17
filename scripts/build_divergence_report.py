#!/usr/bin/env python
# build_divergence_report.py
# Builds a self-contained HTML report that visualises WHERE two deterministic replay runs diverge.
# Inputs: per-tick position dumps (coop_pos_run1/2.txt), hash timelines (coop_replay_run1/2.timeline),
# and the same-tick screenshots + camera transforms captured by the replay verifier
# (coop_shot_run1/2.png, coop_cam_run1/2.json).
#
# It shows (1) a world-space scatter/arrow map of every divergence, and (2) the two same-tick
# screenshots with marker boxes drawn AT the divergent entities' world coordinates.
#
# Usage:
#   python scripts/build_divergence_report.py \
#     --pos1 <game>/coop_pos_run1.txt --pos2 <game>/coop_pos_run2.txt \
#     --tl1  <game>/coop_replay_run1.timeline --tl2 <game>/coop_replay_run2.timeline \
#     --shot1 ../../tabcoop_shots/replay_6666_run1_t168601.png \
#     --shot2 ../../tabcoop_shots/replay_6666_run2_t168601.png \
#     --cam1 <game>/coop_cam_run1.json --cam2 <game>/coop_cam_run2.json \
#     --out analysis/divergence_6666/index.html
import argparse, json, os, hashlib, shutil, datetime, base64, io

def load_pos(path):
    d = {}
    if not path or not os.path.exists(path):
        return d
    with open(path, "r", encoding="utf-8", errors="replace") as f:
        for line in f:
            p = line.rstrip("\n").split("|")
            if len(p) != 4:
                continue
            eid, x, y, typ = p
            try:
                d[eid] = (float(x), float(y), typ)
            except ValueError:
                pass
    return d

def load_timeline(path):
    recs = {}
    if not path or not os.path.exists(path):
        return recs
    with open(path, "r", encoding="utf-8", errors="replace") as f:
        for line in f:
            line = line.strip()
            if not line.startswith("{"):
                continue
            try:
                r = json.loads(line)
                recs[int(r["tick"])] = r
            except Exception:
                pass
    return recs

def load_cam(path):
    if not path or not os.path.exists(path):
        return None
    try:
        with open(path, "r", encoding="utf-8") as f:
            c = json.load(f)
        return c if c.get("valid") else None
    except Exception:
        return None

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--pos1", required=True)
    ap.add_argument("--pos2", required=True)
    ap.add_argument("--tl1", default="")
    ap.add_argument("--tl2", default="")
    ap.add_argument("--shot1", default="")
    ap.add_argument("--shot2", default="")
    ap.add_argument("--cam1", default="")
    ap.add_argument("--cam2", default="")
    ap.add_argument("--manifest", default="", help="tabcoop_shots/latest_replay_shots.json (auto-fills shot/cam paths)")
    ap.add_argument("--out", required=True)
    args = ap.parse_args()

    a = load_pos(args.pos1)
    b = load_pos(args.pos2)

    entities = []
    same = moved = only1 = only2 = 0
    top_moved, max_delta = {}, 0.0

    for eid in set(a) | set(b):
        if eid in a and eid in b:
            x1, y1, t = a[eid]
            x2, y2, _ = b[eid]
            delta = ((x2 - x1) ** 2 + (y2 - y1) ** 2) ** 0.5
            if delta <= 0.01:
                status = 0; same += 1
            else:
                status = 1; moved += 1
                top_moved[t] = top_moved.get(t, 0) + 1
                max_delta = max(max_delta, delta)
            entities.append([eid, t, x1, y1, x2, y2, status, round(delta, 4)])
        elif eid in a:
            x1, y1, t = a[eid]
            only1 += 1
            entities.append([eid, t, x1, y1, None, None, 2, 0])
        else:
            x2, y2, t = b[eid]
            only2 += 1
            entities.append([eid, t, None, None, x2, y2, 3, 0])

    tl1, tl2 = load_timeline(args.tl1), load_timeline(args.tl2)
    common_ticks = sorted(set(tl1) & set(tl2))
    timeline = {"ticks": [], "e1": [], "e2": [], "pos": [], "life": [], "proj": [], "prod": [], "resv": []}
    for t in common_ticks:
        r1, r2 = tl1[t], tl2[t]
        timeline["ticks"].append(t)
        timeline["e1"].append(r1.get("entities", 0))
        timeline["e2"].append(r2.get("entities", 0))
        for key in ("pos", "life", "proj", "prod", "resv"):
            timeline[key].append(1 if r1.get(key) != r2.get(key) else 0)

    if args.manifest:
        try:
            with open(args.manifest, "r", encoding="utf-8") as f:
                man = json.load(f)
            m1 = man.get("run1") or {}
            m2 = man.get("run2") or {}
            if m1.get("shot"): args.shot1 = m1["shot"]
            if m1.get("cam"): args.cam1 = m1["cam"]
            if m2.get("shot"): args.shot2 = m2["shot"]
            if m2.get("cam"): args.cam2 = m2["cam"]
        except Exception as e:
            print(f"warning: manifest read failed: {e}")

    cam1, cam2 = load_cam(args.cam1), load_cam(args.cam2)
    same_cam = bool(cam1 and cam2 and all(abs(cam1[k] - cam2[k]) < 1e-3 for k in ("x", "y", "w", "h")))

    outdir = os.path.dirname(os.path.abspath(args.out))
    os.makedirs(outdir, exist_ok=True)

    def rel_src(p):  # browser URL relative to the report file
        if not p:
            return ""
        ap = p if os.path.isabs(p) else os.path.abspath(p)
        try:
            r = os.path.relpath(ap, outdir)
        except Exception:
            r = p
        return r.replace("\\", "/")

    def embed_shot(p):
        if not p:
            return ""
        ap = p if os.path.isabs(p) else os.path.abspath(p)
        try:
            from PIL import Image
            if not os.path.exists(ap):
                return rel_src(p)
            im = Image.open(ap)
            if im.mode != "RGB":
                im = im.convert("RGB")
            maxw = 1600
            if im.width > maxw:
                im = im.resize((maxw, int(im.height * maxw / im.width)), Image.LANCZOS)
            buf = io.BytesIO()
            im.save(buf, "JPEG", quality=88)
            return "data:image/jpeg;base64," + base64.b64encode(buf.getvalue()).decode("ascii")
        except Exception as e:
            print(f"warning: embed failed ({e}); using relative path")
            return rel_src(p)

    shot1_src, shot2_src = embed_shot(args.shot1), embed_shot(args.shot2)
    generated = datetime.datetime.now().strftime("%Y%m%d_%H%M%S")

    # ---- per-entity-type breakdown + classification + sync impact hints ----
    from collections import defaultdict
    UNIT_HINTS = ["zombie", "ranger", "sniper", "soldier", "worker", "dressed", "weak",
                  "strong", "venom", "harpy", "titan", "lucifer", "mutant", "infected",
                  "veteran", "mayor", "colonel", "engineer", "archer", "swarm", "brute"]
    BUILDING_HINTS = ["house", "center", "tower", "wall", "gate", "cottage", "farm", "hunter",
                      "fisherman", "warehouse", "storehouse", "mill", "foundry", "mine",
                      "radar", "executive", "bank", "market", "inn", "tent", "outpost",
                      "barracks", "stone", "wood", "energy", "power", "storage", "sawmill",
                      "quarry", "lookout", "watchtower", "turret"]

    def classify(ty):
        tl = ty.lower()
        if "proyectile" in tl or "projectile" in tl:
            return "projectile"
        if ty == "DXEntity":
            return "engine"
        for h in UNIT_HINTS:
            if h in tl:
                return "unit"
        for h in BUILDING_HINTS:
            if h in tl:
                return "building"
        return "other"

    IMPACT = {
        "unit": "高 · 单位位置/集合不同 → 战斗、寻路、死亡不同步",
        "projectile": "高 · 投射物位置不同 → 伤害结算不同步（但低 ID 投射物可能是 ID 复用假象）",
        "building": "中 · 建筑位置/集合不同 → 布局/生产/资源不同步",
        "engine": "待核实 · DXEntity 为引擎基类/场景，多为低 ID；疑似跨跑 ID 复用假位移，非真实单位",
        "other": "待核实 · 未归类，需人工确认",
    }

    ts = defaultdict(lambda: {"total": 0, "same": 0, "moved": 0, "only1": 0, "only2": 0, "max": 0.0})
    for e in entities:
        ty, st, delta = e[1], e[6], e[7]
        s = ts[ty]; s["total"] += 1
        if st == 0: s["same"] += 1
        elif st == 1: s["moved"] += 1; s["max"] = max(s["max"], delta)
        elif st == 2: s["only1"] += 1
        else: s["only2"] += 1

    type_stats = []
    for ty, s in ts.items():
        cat = classify(ty)
        type_stats.append([ty, cat, s["total"], s["same"], s["moved"], s["only1"], s["only2"],
                           round(s["max"], 2), IMPACT[cat]])
    type_stats.sort(key=lambda r: (-(r[4] + r[5] + r[6]), -r[2]))

    components = {
        "entities": bool(len(a) != len(b) or only1 or only2),
        "pos": any(timeline["pos"]),
        "life": any(timeline["life"]),
        "proj": any(timeline["proj"]),
        "prod": any(timeline["prod"]),
        "resv": any(timeline["resv"]),
    }

    data = {
        "tick": common_ticks[0] if common_ticks else 0,
        "counts": {"run1": len(a), "run2": len(b), "same": same, "moved": moved,
                   "only1": only1, "only2": only2, "maxDelta": round(max_delta, 3)},
        "topTypes": sorted(top_moved.items(), key=lambda kv: -kv[1])[:25],
        "entities": entities,
        "timeline": timeline,
        "shots": {"a": {"src": shot1_src, "cam": cam1}, "b": {"src": shot2_src, "cam": cam2}},
        "sameCam": same_cam,
        "generated": generated,
        "shotFiles": [os.path.basename(args.shot1) if args.shot1 else "",
                      os.path.basename(args.shot2) if args.shot2 else ""],
        "typeStats": type_stats,
        "components": components,
    }

    doc = HTML_TEMPLATE.replace("__DATA__", json.dumps(data, ensure_ascii=False, separators=(",", ":")))

    # Write a timestamped report so a browser never serves a cached older page.
    base, ext = os.path.splitext(os.path.abspath(args.out))
    if not ext:
        ext = ".html"
    versioned = f"{base}_{generated}{ext}"
    with open(versioned, "w", encoding="utf-8", newline="\n") as f:
        f.write(doc)
    # Also update the requested path for scripts/automation (may be browser-cached).
    try:
        with open(args.out, "w", encoding="utf-8", newline="\n") as f:
            f.write(doc)
    except Exception:
        pass

    print(f"OPEN THIS -> {versioned}")
    print(f"  (also wrote {args.out})")
    print(f"  tick={data['tick']} run1={len(a)} run2={len(b)} same={same} moved={moved} "
          f"only1={only1} only2={only2} maxDelta={max_delta:.3f} camA={bool(cam1)} camB={bool(cam2)} sameCam={same_cam}")
    print(f"  shots: {data['shotFiles'][0]} | {data['shotFiles'][1]}")

HTML_TEMPLATE = r"""<!DOCTYPE html>
<html lang="zh">
<head>
<meta charset="utf-8">
<title>TABCoopMod 确定性分叉可视化</title>
<style>
  :root { --bg:#0f1216; --panel:#171c23; --fg:#e6e9ee; --mut:#8b97a7; --acc:#ffb703; }
  * { box-sizing: border-box; }
  body { margin:0; background:var(--bg); color:var(--fg); font:13px/1.5 "Segoe UI",system-ui,sans-serif; }
  header { padding:12px 18px; background:var(--panel); border-bottom:1px solid #242c36; }
  h1 { font-size:16px; margin:0 0 6px; }
  .mut { color:var(--mut); }
  #meta { color:var(--acc); font-weight:600; margin-top:4px; }
  .explain h3 { margin:10px 0 6px; font-size:14px; }
  table.x { border-collapse:collapse; width:100%; font-size:12px; margin:6px 0; }
  table.x th, table.x td { border:1px solid #2c3642; padding:4px 7px; text-align:left; vertical-align:top; }
  table.x th { background:#1f2731; color:#cdd6e0; position:sticky; top:0; }
  table.x td.n { text-align:right; font-variant-numeric:tabular-nums; }
  .tag { display:inline-block; padding:1px 7px; border-radius:10px; font-size:11px; color:#0b0e12; font-weight:600; }
  .ok { color:#43aa8b; font-weight:600; } .bad { color:#ef476f; font-weight:600; }
  .guide li { margin:2px 0; }
  .scroll { max-height:360px; overflow:auto; }
  .wrap { display:flex; gap:14px; padding:14px 18px; align-items:flex-start; flex-wrap:wrap; }
  .panel { background:var(--panel); border:1px solid #242c36; border-radius:8px; padding:10px; }
  #mapwrap { position:relative; }
  canvas#map { display:block; background:#0b0e12; border-radius:6px; cursor:crosshair; }
  #tip { position:absolute; pointer-events:none; background:#000c; border:1px solid #333; border-radius:6px;
         padding:6px 8px; font-size:12px; display:none; max-width:360px; white-space:nowrap; z-index:9; }
  .stats b { color:var(--acc); }
  .row { display:flex; gap:14px; flex-wrap:wrap; margin:8px 0; align-items:center; }
  .ctl { display:flex; align-items:center; gap:6px; margin:2px 0; }
  select, input[type=range], input[type=number] { background:#0f141a; color:var(--fg); border:1px solid #2c3642; border-radius:5px; padding:2px 6px; }
  .legend span { display:inline-flex; align-items:center; gap:5px; margin-right:12px; }
  .dot { width:10px; height:10px; border-radius:50%; display:inline-block; }
  .line { width:14px; height:3px; display:inline-block; }
  .shots { display:flex; flex-direction:column; gap:8px; }
  .shot { position:relative; width:660px; height:412px; background:#000; border-radius:6px; overflow:hidden; }
  .shot img { position:absolute; inset:0; width:100%; height:100%; object-fit:fill; }
  .ovl { position:absolute; inset:0; }
  .box { position:absolute; border:2px solid #fff; transform:translate(-50%,-50%); box-sizing:border-box; border-radius:2px; pointer-events:none; }
  .box.dash { border-style:dashed; opacity:.6; }
  .shotlabel { position:absolute; left:6px; top:5px; background:#000a; padding:2px 7px; border-radius:4px; font-size:12px; }
  #tl { display:block; background:#0b0e12; border-radius:6px; }
  .tleg span { display:inline-flex; align-items:center; gap:5px; margin-right:10px; }
  .sw { width:10px; height:10px; display:inline-block; border-radius:2px; }
</style>
</head>
<body>
<header>
  <h1>确定性分叉可视化 <span class="mut" id="hdr"></span></h1>
  <div class="mut">左：世界坐标散点/箭头（两跑同一 tick）。右：同 tick 两张截图，方框=按坐标标出的分叉实体（实线=本跑位置，虚线=另一跑位置）。</div>
  <div class="mut" id="meta"></div>
</header>

<div class="wrap"><div class="panel explain" id="explain" style="flex:1; min-width:900px"></div></div>

<div class="wrap">
  <div class="panel" id="mapwrap">
    <div class="row stats" id="stats"></div>
    <div class="row">
      <div class="ctl">最小位移：
        <select id="minDelta">
          <option value="0">全部</option>
          <option value="0.01" selected>&gt;0.01</option>
          <option value="0.1">&gt;0.1</option>
          <option value="0.5">&gt;0.5</option>
          <option value="1">&gt;1</option>
          <option value="5">&gt;5</option>
          <option value="10">&gt;10</option>
        </select>
      </div>
      <div class="ctl">类型：<select id="typeFilter"><option value="">全部</option></select></div>
      <div class="ctl"><label><input type="checkbox" id="showSame" checked> 未移动点</label></div>
      <div class="ctl"><label><input type="checkbox" id="showUnmatched" checked> 独有实体</label></div>
      <div class="ctl">最多框：<input type="number" id="maxBoxes" value="200" min="1" max="5000" style="width:70px"></div>
      <div class="ctl"><button id="reset">重置视图</button></div>
    </div>
    <canvas id="map" width="900" height="820"></canvas>
    <div id="tip"></div>
    <div class="row legend">
      <span><i class="dot" style="background:#4b5563"></i>未移动</span>
      <span><i class="line" style="background:#ffd166"></i>0.01–0.5</span>
      <span><i class="line" style="background:#ff9f1c"></i>0.5–2</span>
      <span><i class="line" style="background:#ef476f"></i>2–10</span>
      <span><i class="line" style="background:#d00000"></i>&gt;10</span>
      <span><i class="dot" style="background:#ef476f"></i>仅run1</span>
      <span><i class="dot" style="background:#4ea8de"></i>仅run2</span>
    </div>
  </div>

  <div class="panel">
    <div class="shots">
      <div class="mut" id="camNote"></div>
      <div class="shot">
        <img id="imgA" alt="run1">
        <div class="ovl" id="ovlA"></div>
        <div class="shotlabel" id="labA">run1</div>
      </div>
      <div class="shot">
        <img id="imgB" alt="run2">
        <div class="ovl" id="ovlB"></div>
        <div class="shotlabel" id="labB">run2</div>
      </div>
      <div class="mut">框颜色同左图图例；框大小随位移增大。悬停方框可见 id/类型/位移。</div>
    </div>
  </div>
</div>

<div class="wrap">
  <div class="panel" style="flex:1; min-width:600px">
    <div class="row"><b>逐 tick 分叉时间线</b><span class="mut">横轴=绝对 tick；线条=实体数(run1实线/run2虚线)；色块=该 tick 各分项不一致</span></div>
    <canvas id="tl" width="1120" height="240"></canvas>
    <div class="row tleg">
      <span><i class="sw" style="background:#ef476f"></i>pos</span>
      <span><i class="sw" style="background:#ff9f1c"></i>life</span>
      <span><i class="sw" style="background:#9b5de5"></i>proj</span>
      <span><i class="sw" style="background:#00bbf9"></i>prod</span>
      <span><i class="sw" style="background:#43aa8b"></i>resv</span>
    </div>
  </div>
</div>

<script>
const DATA = __DATA__;
const canvas = document.getElementById('map');
const ctx = canvas.getContext('2d');
const tip = document.getElementById('tip');
const DPR = window.devicePixelRatio || 1;

let minX=1e9,minY=1e9,maxX=-1e9,maxY=-1e9;
for (const e of DATA.entities){ for (const i of [2,4]){ const x=e[i], y=e[i+1]; if (x==null) continue;
  if(x<minX)minX=x; if(x>maxX)maxX=x; if(y<minY)minY=y; if(y>maxY)maxY=y; } }
if(minX>maxX){minX=0;maxX=1;minY=0;maxY=1;}
const padX=(maxX-minX)*0.03+1, padY=(maxY-minY)*0.03+1;
minX-=padX;maxX+=padX;minY-=padY;maxY+=padY;

const CSSW=900, CSSH=820;
canvas.width=CSSW*DPR; canvas.height=CSSH*DPR;
canvas.style.width=CSSW+'px'; canvas.style.height=CSSH+'px';
let view={scale:1, ox:0, oy:0};
let baseScale=Math.min(CSSW/(maxX-minX), CSSH/(maxY-minY));
function w2s(x,y){ const s=baseScale*view.scale; return [(x-minX)*s+view.ox,(y-minY)*s+view.oy]; }
function fit(){ view.scale=1; const s=baseScale; view.ox=(CSSW-(maxX-minX)*s)/2; view.oy=(CSSH-(maxY-minY)*s)/2; }

let minDelta=0.01, typeFilter='', showSame=true, showUnmatched=true;
function deltaColor(d){ if(d<=0.5)return '#ffd166'; if(d<=2)return '#ff9f1c'; if(d<=10)return '#ef476f'; return '#d00000'; }
function statusColor(e){ return e[6]===1?deltaColor(e[7]):(e[6]===2?'#ef476f':(e[6]===3?'#4ea8de':'#4b5563')); }

function draw(){
  ctx.setTransform(DPR,0,0,DPR,0,0); ctx.clearRect(0,0,CSSW,CSSH);
  ctx.strokeStyle='#161d26'; ctx.lineWidth=1; const step=10;
  for(let gx=Math.ceil(minX/step)*step; gx<=maxX; gx+=step){ const [sx]=w2s(gx,minY); ctx.beginPath(); ctx.moveTo(sx,0); ctx.lineTo(sx,CSSH); ctx.stroke(); }
  for(let gy=Math.ceil(minY/step)*step; gy<=maxY; gy+=step){ const [,sy]=w2s(minX,gy); ctx.beginPath(); ctx.moveTo(0,sy); ctx.lineTo(CSSW,sy); ctx.stroke(); }

  if(showSame){ ctx.fillStyle='#4b5563';
    for(const e of DATA.entities){ if(e[6]!==0||e[2]==null) continue; if(typeFilter&&e[1]!==typeFilter) continue;
      const [sx,sy]=w2s(e[2],e[3]); ctx.beginPath(); ctx.arc(sx,sy,1.0,0,6.2832); ctx.fill(); } }

  ctx.lineWidth=1.2;
  for(const e of DATA.entities){ if(e[6]!==1||e[7]<minDelta) continue; if(typeFilter&&e[1]!==typeFilter) continue;
    const [x1,y1]=w2s(e[2],e[3]), [x2,y2]=w2s(e[4],e[5]); const c=deltaColor(e[7]);
    ctx.strokeStyle=c; ctx.beginPath(); ctx.moveTo(x1,y1); ctx.lineTo(x2,y2); ctx.stroke();
    ctx.fillStyle=c; ctx.beginPath(); ctx.arc(x2,y2,Math.min(4,1.2+Math.log10(1+e[7])*1.4),0,6.2832); ctx.fill(); }

  if(showUnmatched){ for(const e of DATA.entities){
    if(e[6]===2){ if(typeFilter&&e[1]!==typeFilter) continue; const [sx,sy]=w2s(e[2],e[3]); drawX(sx,sy,'#ef476f'); }
    else if(e[6]===3){ if(typeFilter&&e[1]!==typeFilter) continue; const [sx,sy]=w2s(e[4],e[5]); drawX(sx,sy,'#4ea8de'); } } }
  drawStats();
}
function drawX(x,y,c){ ctx.strokeStyle=c; ctx.lineWidth=1.6; ctx.beginPath();
  ctx.moveTo(x-3,y-3); ctx.lineTo(x+3,y+3); ctx.moveTo(x+3,y-3); ctx.lineTo(x-3,y+3); ctx.stroke(); }

function drawStats(){
  const c=DATA.counts, fmt=n=>n.toLocaleString();
  document.getElementById('stats').innerHTML =
    `tick <b>${DATA.tick}</b> · run1 <b>${fmt(c.run1)}</b> · run2 <b>${fmt(c.run2)}</b> · `+
    `未移动 <b>${fmt(c.same)}</b> · 移动 <b>${fmt(c.moved)}</b> · 仅run1 <b style="color:#ef476f">${c.only1}</b> · `+
    `仅run2 <b style="color:#4ea8de">${c.only2}</b> · 最大位移 <b>${c.maxDelta}</b>`;
  document.getElementById('hdr').textContent = `· tick ${DATA.tick}`;
}

// ---------- annotated screenshots ----------
const ovlA=document.getElementById('ovlA'), ovlB=document.getElementById('ovlB');
const camA=DATA.shots.a&&DATA.shots.a.cam, camB=DATA.shots.b&&DATA.shots.b.cam;

// World-grid -> isometric scene space (mirrors DXWorldGrid.ScenePointFromWorldPoint).
// The camera transform exported by CameraProbe is in scene space, so entity world positions must
// be rotated/scaled first, otherwise marker boxes land on the wrong units.
function sceneOf(cam, wx, wy){
  if(wx==null||wy==null) return [wx,wy];
  if(cam && cam.iso && cam.nCells){
    return [ (wx-wy)*0.70710677 + cam.nCells/2, (wx+wy)*0.5 ];
  }
  return [wx, wy];
}

function addBox(ovl, cam, wx, wy, e, dashed){
  if(!cam||wx==null) return false;
  const sp=sceneOf(cam,wx,wy);
  const px=(sp[0]-cam.x)/cam.w, py=(sp[1]-cam.y)/cam.h;
  if(px<0||px>1||py<0||py>1) return false;
  const d=document.createElement('div'); d.className='box'+(dashed?' dash':'');
  d.style.left=(px*100)+'%'; d.style.top=(py*100)+'%';
  const s=8+Math.min(12,Math.log10(1+(e[7]||0))*5);
  d.style.width=s+'px'; d.style.height=s+'px';
  d.style.borderColor=statusColor(e);
  d.title=`${e[1]}  id=${e[0]}  Δ=${e[7]}`;
  ovl.appendChild(d); return true;
}

function inView(cam,wx,wy){
  if(!cam||wx==null) return false;
  const sp=sceneOf(cam,wx,wy);
  const px=(sp[0]-cam.x)/cam.w, py=(sp[1]-cam.y)/cam.h;
  return px>=0&&px<=1&&py>=0&&py<=1;
}

function renderShots(){
  ovlA.innerHTML=''; ovlB.innerHTML='';
  const maxN=Math.max(1,parseInt(document.getElementById('maxBoxes').value||'200',10));
  let cands=DATA.entities.filter(e=>{
    if(e[6]===0) return false;
    if(e[6]===1 && e[7]<minDelta) return false;
    if(typeFilter && e[1]!==typeFilter) return false;
    return true;
  });
  cands.sort((p,q)=>(q[7]||0)-(p[7]||0));
  // Prioritise entities whose positions actually fall inside each screenshot's camera view.
  let nA=0,nB=0;
  for(const e of cands){
    if(nA<maxN && inView(camA,e[2],e[3])){
      addBox(ovlA,camA,e[2],e[3],e,false); nA++;
      if(inView(camA,e[4],e[5])) addBox(ovlA,camA,e[4],e[5],e,true);
    }
    if(nB<maxN && inView(camB,e[4],e[5])){
      addBox(ovlB,camB,e[4],e[5],e,false); nB++;
      if(inView(camB,e[2],e[3])) addBox(ovlB,camB,e[2],e[3],e,true);
    }
  }
  document.getElementById('labA').textContent = `run1 截图 · 视野内框 ${nA}`;
  document.getElementById('labB').textContent = `run2 截图 · 视野内框 ${nB}`;
}

function refresh(){ draw(); renderShots(); }

document.getElementById('minDelta').onchange=e=>{minDelta=parseFloat(e.target.value);refresh();};
document.getElementById('typeFilter').onchange=e=>{typeFilter=e.target.value;refresh();};
document.getElementById('showSame').onchange=e=>{showSame=e.target.checked;draw();};
document.getElementById('showUnmatched').onchange=e=>{showUnmatched=e.target.checked;draw();};
document.getElementById('maxBoxes').onchange=renderShots;
document.getElementById('reset').onclick=()=>{fit();refresh();};

let dragging=false, lastX=0, lastY=0;
canvas.addEventListener('mousedown',e=>{dragging=true;lastX=e.clientX;lastY=e.clientY;});
window.addEventListener('mouseup',()=>dragging=false);
canvas.addEventListener('mousemove',e=>{
  const r=canvas.getBoundingClientRect(); const mx=e.clientX-r.left, my=e.clientY-r.top;
  if(dragging){ view.ox+=e.clientX-lastX; view.oy+=e.clientY-lastY; lastX=e.clientX; lastY=e.clientY; draw(); return; }
  let best=null,bd=144;
  for(const en of DATA.entities){
    if(en[6]===0) continue; if(en[6]===1 && en[7]<minDelta) continue; if(typeFilter&&en[1]!==typeFilter) continue;
    const cx=en[6]===3?en[4]:en[2], cy=en[6]===3?en[5]:en[3]; const [sx,sy]=w2s(cx,cy);
    const d=(sx-mx)*(sx-mx)+(sy-my)*(sy-my); if(d<bd){bd=d;best=en;} }
  if(best){
    const st=['未移动','移动','仅run1','仅run2'][best[6]];
    let t=`<b>${best[1]}</b><br>id ${best[0]}<br>${st}`;
    if(best[6]===1) t+=`<br>位移 ${best[7]}<br>(${best[2]},${best[3]}) → (${best[4]},${best[5]})`;
    else if(best[2]!=null) t+=`<br>run1 (${best[2]},${best[3]})`; else t+=`<br>run2 (${best[4]},${best[5]})`;
    tip.innerHTML=t; tip.style.display='block'; tip.style.left=(mx+14)+'px'; tip.style.top=(my+14)+'px';
  } else tip.style.display='none';
});
canvas.addEventListener('wheel',e=>{ e.preventDefault(); const r=canvas.getBoundingClientRect();
  const mx=e.clientX-r.left,my=e.clientY-r.top; const before=view.scale;
  view.scale*=(e.deltaY<0?1.12:1/1.12); view.scale=Math.max(0.2,Math.min(40,view.scale));
  const k=view.scale/before; view.ox=mx-(mx-view.ox)*k; view.oy=my-(my-view.oy)*k; draw(); },{passive:false});

const sel=document.getElementById('typeFilter');
const types=[...new Set(DATA.entities.filter(e=>e[6]===1).map(e=>e[1]))].sort();
for(const t of types){const o=document.createElement('option');o.value=t;o.textContent=t;sel.appendChild(o);}

// screenshots
document.getElementById('meta').textContent =
  `生成 ${DATA.generated||''} · tick ${DATA.tick} · 截图 ${(DATA.shotFiles&&DATA.shotFiles[0])||''} | ${(DATA.shotFiles&&DATA.shotFiles[1])||''}`;
document.title = `TABCoopMod 分叉报告 · ${DATA.generated||''} · tick ${DATA.tick}`;
if(DATA.shots.a&&DATA.shots.a.src) document.getElementById('imgA').src=DATA.shots.a.src;
if(DATA.shots.b&&DATA.shots.b.src) document.getElementById('imgB').src=DATA.shots.b.src;
document.getElementById('camNote').textContent = (camA&&camB)
  ? (DATA.sameCam ? '两跑相机一致，可直接对比同 tick 画面。' : '两跑相机不同，框按各自相机映射。')
  : '无相机数据，仅显示坐标散点。';

// timeline
function drawTimeline(){
  const c=document.getElementById('tl'), g=c.getContext('2d'); const W=c.width,H=c.height;
  g.clearRect(0,0,W,H); const T=DATA.timeline, N=T.ticks.length;
  if(!N){ g.fillStyle='#8b97a7'; g.fillText('no timeline data',20,30); return; }
  const padL=48,padR=60,padT=16,padB=58, iw=W-padL-padR, ih=H-padT-padB;
  let eMin=1e18,eMax=-1e18; for(const v of T.e1.concat(T.e2)){eMin=Math.min(eMin,v);eMax=Math.max(eMax,v);}
  const ey=v=>padT+ih-((v-eMin)/(Math.max(1,eMax-eMin)))*ih;
  const ex=i=>padL+(N<=1?0:(i/(N-1))*iw);
  g.strokeStyle='#2c3642'; g.lineWidth=1; g.beginPath(); g.moveTo(padL,padT); g.lineTo(padL,padT+ih); g.lineTo(padL+iw,padT+ih); g.stroke();
  g.fillStyle='#8b97a7'; g.font='11px Segoe UI'; g.fillText(eMax.toLocaleString(),4,padT+8); g.fillText(eMin.toLocaleString(),4,padT+ih);
  g.fillText(String(T.ticks[0]),padL,H-34); g.fillText(String(T.ticks[N-1]),padL+iw-40,H-34);
  const bands=[['pos',T.pos,'#ef476f'],['life',T.life,'#ff9f1c'],['proj',T.proj,'#9b5de5'],['prod',T.prod,'#00bbf9'],['resv',T.resv,'#43aa8b']];
  const bh=6, by0=padT+ih+8;
  bands.forEach((b,bi)=>{ g.fillStyle=b[2];
    for(let i=0;i<N;i++){ if(b[1][i]) g.fillRect(ex(i),by0+bi*(bh+2),Math.max(1,iw/N),bh); }
    g.fillStyle='#8b97a7'; g.fillText(b[0],padL+iw+6,by0+bi*(bh+2)+bh-1); });
  g.strokeStyle='#e6e9ee'; g.lineWidth=1.4; g.beginPath(); T.e1.forEach((v,i)=>{const x=ex(i),y=ey(v);i?g.lineTo(x,y):g.moveTo(x,y);}); g.stroke();
  g.strokeStyle='#4ea8de'; g.setLineDash([4,3]); g.beginPath(); T.e2.forEach((v,i)=>{const x=ex(i),y=ey(v);i?g.lineTo(x,y):g.moveTo(x,y);}); g.stroke(); g.setLineDash([]);
}

function esc(s){ return String(s).replace(/[&<>"]/g, c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;'}[c])); }
function catColor(c){ return {unit:'#ff9f1c',projectile:'#9b5de5',building:'#43aa8b',engine:'#8b97a7',other:'#cdd6e0'}[c]||'#888'; }
function catName(c){ return {unit:'单位',projectile:'投射物',building:'建筑',engine:'引擎/场景',other:'其他'}[c]||c; }

function renderExplanations(){
  let h='';
  h+='<h3>一、术语（本页如何标记分叉）</h3>';
  h+='<div class="mut">实体按 64 位 ID 在两跑间对齐。<b>未移动</b>=同 ID 位移≤0.01；<b>移动</b>=同 ID 位移&gt;阈值（箭头）；<b>仅 run1 / 仅 run2</b>=该 ID 只在一跑出现（生成/消失或 ID 分配不同）。</div>';
  h+='<h3>二、事件/分项说明（该 tick 各子系统是否一致）</h3>';
  const comps=[
    ['entities','实体集合','生成/消失/ID 分配是否一致'],
    ['pos','位置 pos','单位移动/寻路是否一致'],
    ['life','生命 life','战斗/死亡是否一致'],
    ['proj','投射物 proj','伤害结算是否一致'],
    ['prod','生产队列 prod','生产是否一致'],
    ['resv','预留 resv','资源分配/工人/食物等'],
  ];
  h+='<table class="x"><tr><th>分项</th><th>含义</th><th>本 tick</th><th>同步影响</th></tr>';
  for(const [k,name,desc] of comps){
    const bad=DATA.components[k];
    h+='<tr><td>'+name+'</td><td class="mut">'+desc+'</td><td class="'+(bad?'bad':'ok')+'">'+(bad?'不一致':'一致')+'</td><td>'+(bad?'可能影响同步':'无影响')+'</td></tr>';
  }
  h+='</table>';
  h+='<h3>三、分叉实体按类型明细（用于判断哪些影响同步）</h3>';
  h+='<div class="mut">按“分叉数(移动+仅出现)”降序；分类与判断为启发式。</div>';
  h+='<div class="scroll"><table class="x"><tr><th>实体类型</th><th>分类</th><th>总数</th><th>未移动</th><th>移动</th><th>仅run1</th><th>仅run2</th><th>最大位移</th><th>是否影响同步（提示）</th></tr>';
  for(const r of DATA.typeStats){
    const ty=r[0],cat=r[1],total=r[2],same=r[3],moved=r[4],o1=r[5],o2=r[6],maxd=r[7],impact=r[8];
    h+='<tr><td>'+esc(ty)+'</td><td><span class="tag" style="background:'+catColor(cat)+'">'+catName(cat)+'</span></td>'+
       '<td class="n">'+total+'</td><td class="n">'+same+'</td><td class="n">'+moved+'</td><td class="n">'+o1+'</td><td class="n">'+o2+'</td><td class="n">'+maxd+'</td>'+
       '<td class="mut">'+esc(impact)+'</td></tr>';
  }
  h+='</table></div>';
  h+='<h3>四、如何快速判断“是否影响同步”</h3>';
  h+='<ul class="guide">'+
     '<li><b>高优先</b>：分类=单位/投射物，且移动或仅出现在某跑 → 战斗、寻路、死亡、伤害会不同步。</li>'+
     '<li><b>中优先</b>：分类=建筑，位置/集合不同 → 布局、生产、资源不同步。</li>'+
     '<li><b>需甄别</b>：类型为 <code>DXEntity</code> 或低 ID 投射物 → 常见为跨跑 <b>ID 复用</b>导致的“假位移”（同 ID 指向不同对象），不一定代表真实单位移动；可用类型过滤器排除后再看。</li>'+
     '<li><b>可靠依据</b>：左侧世界坐标散点/箭头图；右侧截图为背景参考（确定性回放渲染可能不跟随相机/迷雾）。</li>'+
     '</ul>';
  document.getElementById('explain').innerHTML=h;
}

fit(); draw(); renderShots(); drawTimeline(); renderExplanations();
</script>
</body>
</html>
"""

if __name__ == "__main__":
    main()
