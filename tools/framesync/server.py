#!/usr/bin/env python3
"""
BoomNetwork FrameSync Inspector
---------------------------------
帧同步不同步排查工具。接收 VS Demo 客户端上报的 Desync 事件，
自动按帧分组、跨客户端 diff，实时展示在 Web 面板。

用法:
  python3 tools/framesync/server.py [PORT=9878]

端点:
  POST /desync             客户端上报不同步事件（DesyncReporter.cs）
  POST /log                客户端上报 [VS] 日志
  POST /client-logs        批量上报分层日志（VSLogReporter.cs）
  GET  /                   Web 面板（自动打开浏览器）
  GET  /desync             全量事件（JSON array）
  GET  /desync/latest      最近 10 条事件
  GET  /desync/groups      按帧分组 + 自动 diff（供 bn-desync-analyze 拉取）
  GET  /logs               全量日志
  GET  /client-logs        分层日志查询（?channel=X&level=Y&pid=Z&limit=N&since_ts=T）
  GET  /client-logs/channels  返回所有出现过的 channel 列表
  GET  /events             SSE 实时推送
  POST /clear              清空所有数据
"""

import http.server, json, sys, datetime, threading, queue, os, webbrowser
from urllib.parse import urlparse, parse_qs

PORT = int(sys.argv[1]) if len(sys.argv) > 1 else 9878

# ── 全局状态 ──────────────────────────────────────────────────────────────
desync_events: list[dict]       = []   # 按到达顺序，最多 200 条
desync_groups: dict             = {}   # frame(int) → {"events": [...], "diff": {...}}
logs:          list[dict]       = []   # [VS] 日志，最多 2000 条
client_logs:   list[dict]       = []   # 分层客户端日志，最多 5000 条（环形）
sse_clients:   list[queue.Queue] = []
lock = threading.Lock()
client_log_lock = threading.Lock()

G="\033[32m"; R="\033[31m"; Y="\033[33m"; C="\033[36m"; B="\033[1m"; X="\033[0m"
def ts(): return datetime.datetime.now().strftime("%H:%M:%S")

# ── 跨客户端 Diff 计算 ────────────────────────────────────────────────────
def compute_diff(events: list) -> dict:
    """当同一帧有 2 条不同客户端的 desync 事件时，自动计算差异。"""
    if len(events) < 2:
        return {}
    a, b = events[0], events[1]
    sub_a, sub_b = a.get("subsystem", {}), b.get("subsystem", {})
    st_a,  st_b  = a.get("state",     {}), b.get("state",     {})

    keys = ["wave", "players", "enemies", "proj", "gems", "misc", "final"]
    diff_sub = [k for k in keys if sub_a.get(k) != sub_b.get(k)]

    # hash_history 逐帧比较，找第一个分叉帧
    hist_a = {e["f"]: e["h"] for e in a.get("hash_history", [])}
    hist_b = {e["f"]: e["h"] for e in b.get("hash_history", [])}
    common = sorted(set(hist_a) & set(hist_b))
    first_diverge = next((f for f in common if hist_a[f] != hist_b[f]), None)

    wr_a = st_a.get("wave_remaining", 0)
    wr_b = st_b.get("wave_remaining", 0)

    return {
        "pid_a":               a.get("pid", "?"),
        "pid_b":               b.get("pid", "?"),
        "diff_subsystems":     diff_sub,
        "misc_match":          sub_a.get("misc") == sub_b.get("misc"),
        "wr_a":                wr_a,
        "wr_b":                wr_b,
        "wr_diff":             abs(wr_a - wr_b),
        "first_diverge_frame": first_diverge,
        "dt_raw_match":        a.get("dt_raw")       == b.get("dt_raw"),
        "fps_match":           a.get("fps")          == b.get("fps"),
        "tf_match":            a.get("target_frames") == b.get("target_frames"),
        "dt_raw_a":            a.get("dt_raw",  "?"),
        "dt_raw_b":            b.get("dt_raw",  "?"),
        "fps_a":               a.get("fps",     "?"),
        "fps_b":               b.get("fps",     "?"),
        "tf_a":                a.get("target_frames", "?"),
        "tf_b":                b.get("target_frames", "?"),
    }

def add_desync(data: dict):
    """线程安全地存储 desync 事件，更新分组并重算 diff。"""
    frame = int(data.get("frame", -1))
    with lock:
        desync_events.append(data)
        if len(desync_events) > 200:
            desync_events.pop(0)
        if frame not in desync_groups:
            desync_groups[frame] = {"frame": frame, "events": [], "diff": {}}
        grp = desync_groups[frame]
        grp["events"].append(data)
        grp["events"] = grp["events"][-4:]   # 最多保留 4 条（容错）
        grp["diff"] = compute_diff(grp["events"])
        grp["timestamp"] = data.get("timestamp", "")
    return desync_groups[frame]

def broadcast(data: dict):
    payload = "data: " + json.dumps(data, ensure_ascii=False) + "\n\n"
    with lock:
        dead = []
        for q in sse_clients:
            try:   q.put_nowait(payload)
            except queue.Full: dead.append(q)
        for q in dead: sse_clients.remove(q)

# ── Dashboard HTML ────────────────────────────────────────────────────────
DASHBOARD = r"""<!DOCTYPE html>
<html lang="zh">
<head>
<meta charset="UTF-8">
<title>BoomNetwork FrameSync Inspector</title>
<style>
*{box-sizing:border-box;margin:0;padding:0}
body{background:#0d1117;color:#c9d1d9;font-family:'JetBrains Mono','Fira Code',monospace;font-size:13px;display:flex;flex-direction:column;height:100vh}
header{padding:10px 18px;background:#161b22;border-bottom:1px solid #30363d;display:flex;align-items:center;gap:10px;flex-shrink:0}
header h1{font-size:14px;color:#f0f6fc}
#dot{width:8px;height:8px;border-radius:50%;background:#3fb950;flex-shrink:0}
#stats{margin-left:auto;display:flex;gap:12px;font-size:12px}
.stat{color:#8b949e}.stat b{color:#f0f6fc}
.stat.ds b{color:#58a6ff}.stat.ok b{color:#3fb950}
.hdr-btn{padding:3px 10px;background:#21262d;color:#8b949e;border:1px solid #30363d;border-radius:5px;cursor:pointer;font-size:11px;font-family:inherit}
.hdr-btn:hover{background:#30363d;color:#f0f6fc}
/* Tab 切换 */
.tab-bar{display:flex;gap:2px;margin-left:14px}
.tab-btn{padding:3px 14px;background:#21262d;color:#8b949e;border:1px solid #30363d;border-radius:5px;cursor:pointer;font-size:11px;font-family:inherit;letter-spacing:.5px}
.tab-btn.active{background:#1f6feb22;color:#58a6ff;border-color:#1f6feb}
.tab-btn:hover:not(.active){background:#30363d;color:#f0f6fc}
/* 内容区 */
main{display:flex;flex:1;overflow:hidden}
.tab-pane{display:none;flex:1;overflow:hidden}
.tab-pane.active{display:flex}
/* 左：Desync Groups */
#left{flex:1;display:flex;flex-direction:column;border-right:1px solid #21262d;min-width:0;overflow-y:auto;padding:10px 14px;gap:10px}
#left-hdr{padding:7px 0 7px 0;font-size:11px;color:#8b949e;text-transform:uppercase;letter-spacing:.5px;flex-shrink:0;border-bottom:1px solid #30363d;margin-bottom:4px}
#g-empty{text-align:center;padding:40px;color:#484f58}
/* Group card */
.g-card{background:#161b22;border:1px solid #30363d;border-radius:8px;padding:12px 14px}
.g-card.complete{border-color:#1f6feb}
.g-head{display:flex;align-items:baseline;gap:8px;margin-bottom:8px}
.g-frame{font-size:18px;color:#58a6ff;font-weight:700}
.g-meta{font-size:11px;color:#484f58}
.g-clients{display:flex;flex-direction:column;gap:4px;margin-bottom:8px}
.g-client{font-size:11px;padding:4px 8px;background:#0d1117;border-radius:4px;display:grid;grid-template-columns:40px 1fr 1fr 1fr 1fr;gap:8px;align-items:center}
.g-pid{color:#8b949e;font-size:10px}
.mono{font-family:monospace;font-size:11px}
.g-diff{margin-top:8px;border-top:1px solid #30363d;padding-top:8px;font-size:11px}
.g-diff-title{color:#8b949e;margin-bottom:5px;font-size:10px;text-transform:uppercase;letter-spacing:.4px}
.tag{display:inline-block;padding:1px 7px;border-radius:10px;font-size:11px;margin-right:4px;margin-bottom:3px}
.tag.bad{background:#490c0c;color:#f85149}
.tag.ok{background:#0d4429;color:#3fb950}
.tag.warn{background:#2d2000;color:#d29922}
.diff-row{display:flex;flex-wrap:wrap;gap:8px;margin-top:5px;font-size:11px}
.diff-k{color:#484f58;min-width:90px}.diff-v{color:#f0f6fc}
.diff-v.mismatch{color:#f85149}.diff-v.match{color:#3fb950}
/* 右：Console */
#right{width:320px;display:flex;flex-direction:column;flex-shrink:0}
#con-hdr{padding:7px 14px;background:#161b22;border-bottom:1px solid #30363d;font-size:10px;color:#8b949e;text-transform:uppercase;letter-spacing:.5px;display:flex;align-items:center;gap:6px;flex-shrink:0}
#con-filter{margin-left:auto;display:flex;gap:4px}
.flt{padding:1px 6px;border-radius:10px;font-size:10px;cursor:pointer;border:1px solid #30363d;background:#21262d;color:#8b949e}
.flt.active{border-color:#58a6ff;color:#58a6ff}
#console{flex:1;overflow-y:auto;padding:6px 0;font-size:11px;line-height:1.6}
.log-line{padding:1px 12px;white-space:pre-wrap;word-break:break-all}
.log-line.error{color:#f85149}.log-line.warning{color:#d29922}.log-line.log{color:#8b949e}
.log-ts{color:#484f58;margin-right:5px;font-size:10px}
#con-empty{text-align:center;padding:30px;color:#484f58;font-size:11px}
/* LOGS Tab */
#pane-logs{flex-direction:column}
#logs-filter{padding:8px 14px;background:#161b22;border-bottom:1px solid #30363d;display:flex;align-items:center;gap:8px;flex-shrink:0;flex-wrap:wrap}
#logs-filter label{font-size:10px;color:#8b949e}
#log-ch-select{background:#21262d;color:#c9d1d9;border:1px solid #30363d;border-radius:4px;padding:2px 6px;font-size:11px;font-family:inherit}
.lvl-btn{padding:2px 9px;border-radius:10px;font-size:10px;cursor:pointer;border:1px solid #30363d;background:#21262d;color:#8b949e;font-family:inherit}
.lvl-btn.active{border-color:#58a6ff;color:#58a6ff;background:#1f6feb22}
.lvl-btn.err.active{border-color:#f85149;color:#f85149;background:#49040422}
.lvl-btn.warn.active{border-color:#d29922;color:#d29922;background:#2d200022}
#log-pid-input{background:#21262d;color:#c9d1d9;border:1px solid #30363d;border-radius:4px;padding:2px 6px;font-size:11px;font-family:inherit;width:70px}
#log-pid-input::placeholder{color:#484f58}
#logs-count{margin-left:auto;font-size:10px;color:#484f58}
#logs-list{flex:1;overflow-y:auto;padding:4px 0;font-size:11px;line-height:1.7}
.cl-line{padding:2px 14px;white-space:pre-wrap;word-break:break-all;display:flex;gap:8px;align-items:baseline}
.cl-line.ERR{color:#f85149}.cl-line.WARN{color:#d29922}.cl-line.LOG{color:#8b949e}.cl-line.INFO{color:#8b949e}
.cl-ts{color:#484f58;font-size:10px;flex-shrink:0}
.cl-ch{color:#58a6ff;font-size:10px;flex-shrink:0}
.cl-pid{color:#6e7681;font-size:10px;flex-shrink:0}
.cl-msg{flex:1}
#logs-empty{text-align:center;padding:40px;color:#484f58}
</style>
</head>
<body>
<header>
  <div id="dot"></div>
  <h1>BoomNetwork FrameSync Inspector</h1>
  <div class="tab-bar">
    <button class="tab-btn active" id="tab-desync" onclick="switchTab('desync')">DESYNC</button>
    <button class="tab-btn" id="tab-logs" onclick="switchTab('logs')">LOGS</button>
  </div>
  <div id="stats">
    <span class="stat ds">Desync帧 <b id="s-groups">0</b></span>
    <span class="stat">事件 <b id="s-events">0</b></span>
    <span class="stat ok">完整diff <b id="s-diffs">0</b></span>
  </div>
  <button class="hdr-btn" onclick="clearAll()">清空</button>
</header>
<main>
  <!-- DESYNC Tab -->
  <div id="pane-desync" class="tab-pane active">
    <!-- 左：Desync Groups -->
    <div id="left">
      <div id="left-hdr">Desync 帧分组（鼠标悬停 history 详情）</div>
      <div id="g-empty">等待 Desync 上报…</div>
    </div>
    <!-- 右：Console -->
    <div id="right">
      <div id="con-hdr">
        Console
        <div id="con-filter">
          <span class="flt active" data-level="all"     onclick="setFilter('all')">全部</span>
          <span class="flt"        data-level="error"   onclick="setFilter('error')">Err</span>
          <span class="flt"        data-level="warning" onclick="setFilter('warning')">Warn</span>
          <span class="flt"        data-level="log"     onclick="setFilter('log')">Log</span>
        </div>
      </div>
      <div id="console"><div id="con-empty">等待日志上报…</div></div>
    </div>
  </div>
  <!-- LOGS Tab -->
  <div id="pane-logs" class="tab-pane">
    <div id="logs-filter">
      <label>Channel</label>
      <select id="log-ch-select" onchange="applyLogsFilter()">
        <option value="">全部</option>
      </select>
      <label style="margin-left:4px">Level</label>
      <button class="lvl-btn active" data-lv="" onclick="setLvFilter(this,'')">全部</button>
      <button class="lvl-btn err"    data-lv="ERR"  onclick="setLvFilter(this,'ERR')">ERR</button>
      <button class="lvl-btn warn"   data-lv="WARN" onclick="setLvFilter(this,'WARN')">WARN</button>
      <button class="lvl-btn"        data-lv="LOG"  onclick="setLvFilter(this,'LOG')">LOG</button>
      <label style="margin-left:4px">PID</label>
      <input id="log-pid-input" type="text" placeholder="全部" oninput="applyLogsFilter()">
      <button class="hdr-btn" onclick="clearClientLogs()">清空</button>
      <span id="logs-count">0 条</span>
    </div>
    <div id="logs-list"><div id="logs-empty">等待客户端日志上报…</div></div>
  </div>
</main>
<script>
let gCount=0, eCount=0, dCount=0, curFilter='all';
const MAX_LOG=1000;
const MAX_CLIENT_LOG=2000;
let _logsLoaded=false;
let _allClientLogs=[];
let _lvFilter='', _chFilter='', _pidFilter='';

function esc(s){return String(s||'').replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;');}

// ── Tab 切换 ─────────────────────────────────────────────────────────────
function switchTab(name){
  document.querySelectorAll('.tab-pane').forEach(p=>p.classList.remove('active'));
  document.querySelectorAll('.tab-btn').forEach(b=>b.classList.remove('active'));
  document.getElementById('pane-'+name).classList.add('active');
  document.getElementById('tab-'+name).classList.add('active');
  if(name==='logs'&&!_logsLoaded){ loadClientLogs(); }
}

// ── DESYNC Tab ────────────────────────────────────────────────────────────
function renderGroup(g){
  document.getElementById('g-empty').style.display='none';
  const d=g.diff||{}, evts=g.events||[];
  const hasDiff=Object.keys(d).length>0;

  let clientsHtml=evts.map(ev=>{
    const sub=ev.subsystem||{}, st=ev.state||{};
    return `<div class="g-client">
      <span class="g-pid">P${esc(ev.pid)}</span>
      <span class="mono" style="color:#58a6ff">${esc(sub.final||'?')}</span>
      <span class="mono" style="color:#d29922">wave=${esc(sub.wave||'?')}</span>
      <span class="mono">wr=${esc(st.wave_remaining||'?')}</span>
      <span class="mono" style="color:#8b949e">rng=${esc(st.rng||'?')}</span>
    </div>`;
  }).join('');

  let diffHtml='';
  if(hasDiff){
    const sub_tags=(d.diff_subsystems||[]).map(s=>`<span class="tag bad">${esc(s)}</span>`).join('');
    const misc_tag=d.misc_match?'<span class="tag ok">misc ✓</span>':'<span class="tag bad">misc ✗</span>';
    const dt_tag=d.dt_raw_match?'<span class="tag ok">dt_raw ✓</span>':`<span class="tag bad">dt_raw ✗ (${esc(d.dt_raw_a)}≠${esc(d.dt_raw_b)})</span>`;
    const fps_tag=d.fps_match?'<span class="tag ok">fps ✓</span>':`<span class="tag bad">fps ✗ (${esc(d.fps_a)}≠${esc(d.fps_b)})</span>`;
    const tf_tag=d.tf_match?'<span class="tag ok">tf ✓</span>':`<span class="tag bad">tf ✗ (${esc(d.tf_a)}≠${esc(d.tf_b)})</span>`;
    const wr_color=d.wr_diff>0?'#f85149':'#3fb950';
    const fdiv=d.first_diverge_frame!=null
      ?`<span class="tag warn">首次分叉帧 ${esc(d.first_diverge_frame)}</span>`
      :'<span class="tag ok">history 无分叉</span>';
    diffHtml=`<div class="g-diff">
      <div class="g-diff-title">跨客户端 Diff</div>
      <div>${sub_tags}${misc_tag}${dt_tag}${fps_tag}${tf_tag}${fdiv}</div>
      <div class="diff-row" style="margin-top:6px">
        <span class="diff-k">P${esc(d.pid_a)} wr</span><span class="diff-v">${esc(d.wr_a)}</span>
        <span class="diff-k">P${esc(d.pid_b)} wr</span><span class="diff-v">${esc(d.wr_b)}</span>
        <span class="diff-k">wr_diff</span><span class="diff-v" style="color:${wr_color};font-weight:600">${esc(d.wr_diff)}</span>
      </div>
    </div>`;
  }

  const hist=evts.map(ev=>(ev.hash_history||[]).map(e=>`f=${e.f} h=${e.h} wr=${e.wr}`).join('\n')).join('\n─\n');

  const card=document.createElement('div');
  card.className='g-card'+(hasDiff?' complete':'');
  card.dataset.frame=g.frame;
  card.title=hist;
  card.innerHTML=`
    <div class="g-head">
      <span class="g-frame">Frame ${esc(g.frame)}</span>
      <span class="g-meta">${esc(g.timestamp||'')} · ${esc(evts.length)} client(s)</span>
    </div>
    <div class="g-clients">${clientsHtml}</div>
    ${diffHtml}`;
  return card;
}

function upsertGroup(g){
  const existing=document.querySelector(`[data-frame="${g.frame}"]`);
  const card=renderGroup(g);
  const left=document.getElementById('left');
  if(existing){
    left.replaceChild(card, existing);
  } else {
    const empty=document.getElementById('g-empty');
    left.insertBefore(card, empty.nextSibling);
    gCount++;document.getElementById('s-groups').textContent=gCount;
  }
  eCount++;document.getElementById('s-events').textContent=eCount;
  if(g.diff&&Object.keys(g.diff).length){dCount++;document.getElementById('s-diffs').textContent=dCount;}
}

function addLog(d,prepend){
  document.getElementById('con-empty').style.display='none';
  const con=document.getElementById('console');
  const div=document.createElement('div');
  div.className='log-line '+(d.level||'log');
  div.dataset.level=d.level||'log';
  div.innerHTML=`<span class="log-ts">${esc(d.timestamp||'')}</span>${esc(d.message||'')}`;
  if(curFilter!=='all'&&d.level!==curFilter) div.style.display='none';
  prepend?con.insertBefore(div,con.firstChild):con.appendChild(div);
  if(!prepend) con.scrollTop=con.scrollHeight;
  while(con.children.length>MAX_LOG+1) con.removeChild(con.lastChild);
}
function setFilter(level){
  curFilter=level;
  document.querySelectorAll('.flt').forEach(el=>el.classList.toggle('active',el.dataset.level===level));
  document.querySelectorAll('.log-line').forEach(el=>{
    el.style.display=(level==='all'||el.dataset.level===level)?'':'none';
  });
}

// ── LOGS Tab ──────────────────────────────────────────────────────────────
function fmtTs(ts){
  if(!ts) return '';
  const d=new Date(ts);
  if(isNaN(d)) return String(ts);
  return d.toTimeString().slice(0,8)+'.'+String(d.getMilliseconds()).padStart(3,'0');
}

function addClientLog(entry, scroll){
  _allClientLogs.push(entry);
  if(_allClientLogs.length>MAX_CLIENT_LOG) _allClientLogs.splice(0, _allClientLogs.length-MAX_CLIENT_LOG);
  // Update channel dropdown
  const sel=document.getElementById('log-ch-select');
  const ch=entry.channel||'';
  if(ch && !Array.from(sel.options).some(o=>o.value===ch)){
    const opt=document.createElement('option');
    opt.value=ch; opt.textContent=ch;
    sel.appendChild(opt);
  }
  // Check if passes current filter
  if(!entryPassesFilter(entry)) return;
  const list=document.getElementById('logs-list');
  document.getElementById('logs-empty').style.display='none';
  const lv=(entry.level||'LOG').toUpperCase();
  const div=document.createElement('div');
  div.className='cl-line '+lv;
  div.dataset.ch=entry.channel||'';
  div.dataset.lv=lv;
  div.dataset.pid=String(entry.pid||'');
  div.innerHTML=`<span class="cl-ts">${esc(fmtTs(entry.ts))}</span><span class="cl-ch">[${esc(entry.channel||'')}]</span><span class="cl-pid">pid=${esc(entry.pid||'')}</span><span class="cl-msg">${esc(entry.msg||'')}</span>`;
  list.appendChild(div);
  // Trim rendered list
  const items=list.querySelectorAll('.cl-line');
  if(items.length>MAX_CLIENT_LOG) items[0].remove();
  if(scroll) list.scrollTop=list.scrollHeight;
  updateLogsCount();
}

function entryPassesFilter(entry){
  const lv=(entry.level||'LOG').toUpperCase();
  if(_lvFilter && lv!==_lvFilter) return false;
  if(_chFilter && (entry.channel||'')!==_chFilter) return false;
  if(_pidFilter && String(entry.pid||'')!==_pidFilter) return false;
  return true;
}

function applyLogsFilter(){
  _chFilter=document.getElementById('log-ch-select').value;
  _pidFilter=document.getElementById('log-pid-input').value.trim();
  rebuildLogsList();
}

function setLvFilter(btn, lv){
  _lvFilter=lv;
  document.querySelectorAll('.lvl-btn').forEach(b=>b.classList.remove('active'));
  btn.classList.add('active');
  rebuildLogsList();
}

function rebuildLogsList(){
  const list=document.getElementById('logs-list');
  list.innerHTML='<div id="logs-empty" style="display:none">等待客户端日志上报…</div>';
  const filtered=_allClientLogs.filter(entryPassesFilter);
  if(filtered.length===0){
    document.getElementById('logs-empty').style.display='';
  }
  for(const e of filtered){
    const lv=(e.level||'LOG').toUpperCase();
    const div=document.createElement('div');
    div.className='cl-line '+lv;
    div.dataset.ch=e.channel||'';
    div.dataset.lv=lv;
    div.dataset.pid=String(e.pid||'');
    div.innerHTML=`<span class="cl-ts">${esc(fmtTs(e.ts))}</span><span class="cl-ch">[${esc(e.channel||'')}]</span><span class="cl-pid">pid=${esc(e.pid||'')}</span><span class="cl-msg">${esc(e.msg||'')}</span>`;
    list.appendChild(div);
  }
  list.scrollTop=list.scrollHeight;
  updateLogsCount();
}

function updateLogsCount(){
  const visible=document.getElementById('logs-list').querySelectorAll('.cl-line').length;
  document.getElementById('logs-count').textContent=`${visible} / ${_allClientLogs.length} 条`;
}

function loadClientLogs(){
  _logsLoaded=true;
  fetch('/client-logs?limit=500').then(r=>r.json()).then(list=>{
    for(const e of list) addClientLog(e, false);
    const listEl=document.getElementById('logs-list');
    listEl.scrollTop=listEl.scrollHeight;
    updateLogsCount();
  });
  // Also populate channel dropdown
  fetch('/client-logs/channels').then(r=>r.json()).then(data=>{
    const sel=document.getElementById('log-ch-select');
    (data.channels||[]).forEach(ch=>{
      if(!Array.from(sel.options).some(o=>o.value===ch)){
        const opt=document.createElement('option');
        opt.value=ch; opt.textContent=ch;
        sel.appendChild(opt);
      }
    });
  });
}

function clearClientLogs(){
  _allClientLogs=[];
  _logsLoaded=false;
  document.getElementById('logs-list').innerHTML='<div id="logs-empty">等待客户端日志上报…</div>';
  updateLogsCount();
}

function clearAll(){
  fetch('/clear',{method:'POST'}).then(()=>{
    document.querySelectorAll('.g-card').forEach(c=>c.remove());
    document.getElementById('g-empty').style.display='';
    document.getElementById('console').innerHTML='<div id="con-empty">等待日志上报…</div>';
    gCount=eCount=dCount=0;
    ['s-groups','s-events','s-diffs'].forEach(id=>document.getElementById(id).textContent=0);
    _allClientLogs=[];
    _logsLoaded=false;
    document.getElementById('logs-list').innerHTML='<div id="logs-empty">等待客户端日志上报…</div>';
    updateLogsCount();
  });
}

// 初始加载
fetch('/desync/groups').then(r=>r.json()).then(gs=>gs.forEach(g=>upsertGroup(g)));
fetch('/logs').then(r=>r.json()).then(list=>list.forEach(d=>addLog(d,false)));

// SSE
const es=new EventSource('/events');
es.onmessage=e=>{
  const d=JSON.parse(e.data);
  if(d._type==='desync_group')  upsertGroup(d.group);
  else if(d._type==='log')      addLog(d, false);
  else if(d._type==='client_log') addClientLog(d.entry, true);
  else if(d._type==='clear'){
    document.querySelectorAll('.g-card').forEach(c=>c.remove());
    document.getElementById('g-empty').style.display='';
    document.getElementById('console').innerHTML='<div id="con-empty">等待日志上报…</div>';
    gCount=eCount=dCount=0;
    ['s-groups','s-events','s-diffs'].forEach(id=>document.getElementById(id).textContent=0);
    _allClientLogs=[];
    _logsLoaded=false;
    document.getElementById('logs-list').innerHTML='<div id="logs-empty">等待客户端日志上报…</div>';
    updateLogsCount();
  }
};
es.onerror=()=>document.getElementById('dot').style.background='#f85149';
</script>
</body>
</html>"""

# ── Handler ───────────────────────────────────────────────────────────────
class Handler(http.server.BaseHTTPRequestHandler):

    def do_OPTIONS(self):
        self._cors(); self.end_headers()

    def do_POST(self):
        path   = urlparse(self.path).path
        length = int(self.headers.get("Content-Length", 0))
        body   = self.rfile.read(length)

        if path == "/desync":
            try: data = json.loads(body)
            except Exception as e: return self._respond(400, str(e))
            grp = add_desync(data)
            d = grp.get("diff", {})
            flag = f"{C}DIFF{X}" if d else f"{Y}wait{X}"
            print(f"  [{ts()}] {B}DESYNC{X} frame={data.get('frame','?')} pid={data.get('pid','?')} "
                  f"final={data.get('subsystem',{}).get('final','?')} {flag}")
            if d.get("first_diverge_frame") is not None:
                print(f"  {R}  → 首次分叉帧: {d['first_diverge_frame']}  diff_sub: {d.get('diff_subsystems',[])}  wr_diff: {d.get('wr_diff',0)}{X}")
            broadcast({"_type": "desync_group", "group": grp})
            self._cors(); self._respond(200, "ok")

        elif path == "/log":
            try: data = json.loads(body)
            except Exception as e: return self._respond(400, str(e))
            # 只保留 [VS] 相关日志，过滤其他噪音
            msg = data.get("message", "")
            if "[VS]" in msg or data.get("level") in ("error", "warning"):
                data["_type"] = "log"
                with lock:
                    logs.append(data)
                    if len(logs) > 2000: logs.pop(0)
                broadcast(data)
            self._cors(); self._respond(200, "ok")

        elif path == "/client-logs":
            try:
                entries = json.loads(body)
            except Exception as e:
                return self._respond(400, f"JSON parse error: {e}")
            if not isinstance(entries, list):
                return self._respond(400, "body must be a JSON array")
            # Validate required fields
            valid = []
            for item in entries[:200]:  # 单次最多 200 条
                if not isinstance(item, dict):
                    continue
                if "ts" not in item or "channel" not in item or "level" not in item or "msg" not in item:
                    return self._respond(400, "each entry must have ts, channel, level, msg")
                entry = {
                    "ts":      item["ts"],
                    "channel": item["channel"],
                    "level":   item["level"],
                    "pid":     item.get("pid", -1),
                    "msg":     item["msg"],
                    "extra":   item.get("extra", {}),
                }
                valid.append(entry)
            with client_log_lock:
                client_logs.extend(valid)
                if len(client_logs) > 5000:
                    del client_logs[:len(client_logs) - 5000]
            for entry in valid:
                broadcast({"_type": "client_log", "entry": entry})
            self._cors(); self._respond(200, "ok")

        elif path == "/clear":
            with lock:
                desync_events.clear()
                desync_groups.clear()
                logs.clear()
            with client_log_lock:
                client_logs.clear()
            broadcast({"_type": "clear"})
            print(f"  {Y}[{ts()}] 已清空{X}")
            self._cors(); self._respond(200, "cleared")

        else:
            self._respond(404, "not found")

    def do_GET(self):
        path = urlparse(self.path).path

        if path == "/":
            self._html(DASHBOARD)

        elif path == "/desync":
            with lock: data = json.dumps(desync_events, ensure_ascii=False)
            self._json(data)

        elif path == "/desync/latest":
            with lock: snap = list(desync_events[-10:])
            self._json(json.dumps(snap, ensure_ascii=False))

        elif path == "/desync/groups":
            with lock: snap = list(desync_groups.values())
            snap.sort(key=lambda g: g.get("frame", 0))
            self._json(json.dumps(snap, ensure_ascii=False))

        elif path == "/logs":
            with lock: data = json.dumps(logs, ensure_ascii=False)
            self._json(data)

        elif path == "/client-logs":
            qs = parse_qs(urlparse(self.path).query)
            f_channel  = qs["channel"][0]  if "channel"  in qs else None
            f_level    = qs["level"][0]    if "level"    in qs else None
            f_pid      = int(qs["pid"][0]) if "pid"      in qs else None
            f_since_ts = int(qs["since_ts"][0]) if "since_ts" in qs else None
            try:
                limit = min(int(qs["limit"][0]), 500) if "limit" in qs else 200
            except (ValueError, KeyError):
                limit = 200
            with client_log_lock:
                snap = list(client_logs)
            result = []
            for e in snap:
                if f_channel  is not None and e["channel"] != f_channel:   continue
                if f_level    is not None and e["level"]   != f_level:     continue
                if f_pid      is not None and e["pid"]     != f_pid:       continue
                if f_since_ts is not None and e["ts"]      <  f_since_ts:  continue
                result.append(e)
            result.sort(key=lambda x: x["ts"])
            self._json(json.dumps(result[-limit:] if len(result) > limit else result, ensure_ascii=False))

        elif path == "/client-logs/channels":
            with client_log_lock:
                channels = list({e["channel"] for e in client_logs})
            channels.sort()
            self._json(json.dumps({"channels": channels}, ensure_ascii=False))

        elif path == "/events":
            self.send_response(200)
            self.send_header("Content-Type",  "text/event-stream")
            self.send_header("Cache-Control", "no-cache")
            self.send_header("Connection",    "keep-alive")
            self._cors(); self.end_headers()
            q: queue.Queue = queue.Queue(maxsize=100)
            with lock: sse_clients.append(q)
            try:
                while True:
                    try:
                        payload = q.get(timeout=20)
                        self.wfile.write(payload.encode()); self.wfile.flush()
                    except queue.Empty:
                        self.wfile.write(b": heartbeat\n\n"); self.wfile.flush()
            except (BrokenPipeError, ConnectionResetError):
                pass
            finally:
                with lock:
                    if q in sse_clients: sse_clients.remove(q)

        else:
            self._respond(404, "not found")

    def _cors(self):
        self.send_header("Access-Control-Allow-Origin",  "*")
        self.send_header("Access-Control-Allow-Methods", "GET, POST, OPTIONS")
        self.send_header("Access-Control-Allow-Headers", "Content-Type")

    def _respond(self, code: int, body: str):
        b = body.encode()
        self.send_response(code)
        self.send_header("Content-Type", "text/plain; charset=utf-8")
        self.send_header("Content-Length", str(len(b)))
        self.end_headers(); self.wfile.write(b)

    def _json(self, data: str):
        b = data.encode()
        self.send_response(200)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(b)))
        self._cors(); self.end_headers(); self.wfile.write(b)

    def _html(self, html: str):
        b = html.encode()
        self.send_response(200)
        self.send_header("Content-Type", "text/html; charset=utf-8")
        self.send_header("Content-Length", str(len(b)))
        self.end_headers(); self.wfile.write(b)

    def log_message(self, fmt, *args): pass  # 屏蔽默认访问日志

# ── 启动 ─────────────────────────────────────────────────────────────────
class ThreadingServer(http.server.ThreadingHTTPServer):
    daemon_threads      = True
    allow_reuse_address = True

if __name__ == "__main__":
    server = ThreadingServer(("0.0.0.0", PORT), Handler)
    print(f"\n{B}{C}BoomNetwork FrameSync Inspector{X}")
    print(f"  Web 面板  →  {B}http://localhost:{PORT}/{X}")
    print(f"  /desync/groups  →  双端 Diff 数据（供 bn-desync-analyze）")
    print(f"  按 Ctrl+C 停止\n")
    webbrowser.open(f"http://localhost:{PORT}/")
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        print(f"\n{Y}已停止{X}")
