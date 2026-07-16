namespace PackageManager.Features.CommandPalette.Views
{
    /// <summary>
    /// 命令面板的 HTML/CSS/JS（单文件），由 WebView2 以 NavigateToString 加载。
    /// 复用 docs/command-palette-prototype.html 的视觉与交互；数据源与执行改为 JS⇄C# 桥接。
    /// </summary>
    internal static class PaletteHtml
    {
        public static string Build()
        {
            return @"<!DOCTYPE html>
<html lang='zh-CN'>
<head>
<meta charset='UTF-8'/>
<meta name='viewport' content='width=device-width, initial-scale=1.0'/>
<style>
:root{
  --overlay:rgba(8,9,11,.55); --card:#212327; --card-2:#1a1c1f; --line:#34373d;
  --txt:#e7e9ee; --txt-dim:#9aa0aa; --txt-faint:#62666e; --accent:#13b8a6; --sel:#2b2f36;
  --mono:'Cascadia Code','JetBrains Mono','Consolas',monospace;
  --sans:'Segoe UI','Microsoft YaHei',system-ui,sans-serif;
}
*{box-sizing:border-box}
html,body{margin:0;height:100%;font-family:var(--sans);color:var(--txt);overflow:hidden}
body{background:#0e0f12;display:flex;justify-content:center;align-items:stretch;padding:10px;box-sizing:border-box}
.palette{width:100%;height:100%;background:var(--card);border:1px solid var(--line);border-radius:12px;
  box-shadow:0 12px 40px rgba(0,0,0,.45);overflow:hidden;display:flex;flex-direction:column}
@keyframes pop{from{transform:translateY(-10px) scale(.99);opacity:0}to{transform:none;opacity:1}}
.input-row{display:flex;align-items:center;gap:10px;padding:14px 16px;border-bottom:1px solid var(--line)}
.mode-badge{display:inline-flex;align-items:center;gap:6px;font-size:12px;font-weight:600;padding:4px 9px;border-radius:7px;background:#2a2d33;color:var(--txt-dim);white-space:nowrap}
.mode-badge .dot{width:7px;height:7px;border-radius:50%;background:var(--txt-faint)}
#q{flex:1;background:transparent;border:0;outline:0;color:var(--txt);font-size:17px}
#q::placeholder{color:var(--txt-faint)}
.results{flex:1;overflow-y:auto}
.results::-webkit-scrollbar{width:9px}
.results::-webkit-scrollbar-thumb{background:#3a3d44;border-radius:6px;border:2px solid var(--card)}
.group{padding:6px 0}
.group-head{display:flex;align-items:center;gap:8px;padding:9px 16px 5px;font-size:11px;font-weight:700;letter-spacing:.6px;color:var(--txt-faint);text-transform:uppercase}
.group-head .cnt{color:var(--txt-faint);font-weight:600;background:#26292f;padding:1px 7px;border-radius:20px}
.group-head svg{width:15px;height:15px;opacity:.85}
.item{display:flex;align-items:center;gap:12px;padding:9px 16px;cursor:pointer;border-left:2px solid transparent}
.item.sel{background:var(--sel);border-left-color:var(--accent)}
.item.sel .title{color:#fff}
.ic{width:30px;height:30px;border-radius:8px;display:flex;align-items:center;justify-content:center;flex:0 0 auto}
.ic svg{width:16px;height:16px}
.body{flex:1;min-width:0}
.title{font-size:14px;color:var(--txt);white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
.title mark{background:transparent;color:var(--accent);font-weight:700}
.sub{font-size:12px;color:var(--txt-faint);font-family:var(--mono);white-space:nowrap;overflow:hidden;text-overflow:ellipsis;margin-top:1px}
.tags{display:flex;gap:6px;align-items:center;flex:0 0 auto}
.tag{font-size:10.5px;font-weight:600;padding:2px 7px;border-radius:20px;white-space:nowrap}
.hk{font:600 11px var(--mono);color:var(--txt-faint)}
.badge-new{background:rgba(19,184,166,.16);color:#2dd4bf}
.badge-warn{background:rgba(245,158,11,.16);color:#fbbf24}
.badge-ahead{background:rgba(74,158,255,.16);color:#60a5fa}
.badge-st{background:#2a2d33;color:var(--txt-dim)}
.empty{padding:42px 16px;text-align:center;color:var(--txt-faint)}
.empty .big{font-size:14px;color:var(--txt-dim);margin-bottom:5px}
.searching{padding:22px;text-align:center;color:var(--txt-faint);font-size:13px}
.searching .sp{display:inline-block;width:13px;height:13px;border:2px solid #3a3d44;border-top-color:var(--accent);border-radius:50%;animation:rot .7s linear infinite;vertical-align:-2px;margin-right:7px}
@keyframes rot{to{transform:rotate(360deg)}}
.footer{display:flex;align-items:center;gap:14px;padding:9px 16px;border-top:1px solid var(--line);background:var(--card-2);font-size:11.5px;color:var(--txt-faint)}
.footer .seg{display:flex;align-items:center;gap:6px}
.footer .sp{flex:1}
.c-cmd{background:rgba(74,158,255,.14);color:#6cb0ff}
.c-nav{background:rgba(148,163,184,.14);color:#aab6c5}
.c-pkg{background:rgba(19,184,166,.14);color:#2dd4bf}
.c-file{background:rgba(245,158,11,.14);color:#fbbf24}
</style>
</head>
<body>
  <div class='palette'>
    <div class='input-row'>
      <span class='mode-badge' id='modeBadge'><span class='dot'></span><span id='modeText'>全部</span></span>
      <input id='q' autocomplete='off' spellcheck='false' placeholder='搜索 命令 / 导航 / 包 / 文件…  （ > 命令   # 包   / 文件 ）'/>
    </div>
    <div class='results' id='results'></div>
    <div class='footer'>
      <span class='seg'><span id='footCount'>0</span> 项</span>
      <span class='sp'></span>
      <span class='seg' id='footHint'>↑↓ 选择 · Enter 执行 · Tab 打开详情 · Esc 关闭</span>
    </div>
  </div>
<script>
(function(){
var IC={
  cmd:'<svg viewBox=\'0 0 24 24\' fill=\'none\' stroke=\'currentColor\' stroke-width=\'2\' stroke-linecap=\'round\' stroke-linejoin=\'round\'><polyline points=\'4 17 10 11 4 5\'/><line x1=\'12\' y1=\'19\' x2=\'20\' y2=\'19\'/></svg>',
  nav:'<svg viewBox=\'0 0 24 24\' fill=\'none\' stroke=\'currentColor\' stroke-width=\'2\' stroke-linecap=\'round\' stroke-linejoin=\'round\'><rect x=\'3\' y=\'3\' width=\'7\' height=\'7\'/><rect x=\'14\' y=\'3\' width=\'7\' height=\'7\'/><rect x=\'3\' y=\'14\' width=\'7\' height=\'7\'/><rect x=\'14\' y=\'14\' width=\'7\' height=\'7\'/></svg>',
  pkg:'<svg viewBox=\'0 0 24 24\' fill=\'none\' stroke=\'currentColor\' stroke-width=\'2\' stroke-linecap=\'round\' stroke-linejoin=\'round\'><path d=\'M21 16V8a2 2 0 0 0-1-1.73l-7-4a2 2 0 0 0-2 0l-7 4A2 2 0 0 0 3 8v8a2 2 0 0 0 1 1.73l7 4a2 2 0 0 0 2 0l7-4A2 2 0 0 0 21 16z\'/><polyline points=\'3.27 6.96 12 12.01 20.73 6.96\'/><line x1=\'12\' y1=\'22.08\' x2=\'12\' y2=\'12\'/></svg>',
  file:'<svg viewBox=\'0 0 24 24\' fill=\'none\' stroke=\'currentColor\' stroke-width=\'2\' stroke-linecap=\'round\' stroke-linejoin=\'round\'><path d=\'M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8z\'/><polyline points=\'14 2 14 8 20 8\'/></svg>'
};
IC['pkg-cmd']=IC.pkg; IC['pkg-param']=IC.pkg;
var CAT={
  cmd:{name:'命令',cls:'c-cmd',order:2},
  'pkg-cmd':{name:'包操作',cls:'c-pkg',order:2},
  nav:{name:'导航',cls:'c-nav',order:3},
  pkg:{name:'产品包',cls:'c-pkg',order:4},
  'pkg-param':{name:'选项',cls:'c-pkg',order:4},
  file:{name:'文件',cls:'c-file',order:7}
};
var PREFIX={'>':'cmd','#':'pkg','@':'work','/':'file'};
var RECENT_KEY='pm_palette_recent_v1';
var recent=loadRecent();
var q='',mode=null,flat=[],sel=0,subStack=[];
function topSub(){return subStack.length?subStack[subStack.length-1]:null;}

function loadRecent(){try{var s=localStorage.getItem(RECENT_KEY);return s?JSON.parse(s):{};}catch(e){return{};}}
function saveRecent(){try{localStorage.setItem(RECENT_KEY,JSON.stringify(recent));}catch(e){}}
function rk(d){return d.Type+':'+d.Title;}
function isRecent(d){return !!recent[rk(d)];}
function pushRecent(d){recent[rk(d)]=Date.now();var ks=Object.keys(recent).sort(function(a,b){return recent[b]-recent[a];});if(ks.length>12){for(var i=12;i<ks.length;i++) delete recent[ks[i]];}saveRecent();}

function fuzzy(text,qstr){if(!qstr) return {score:1,marks:[]};text=text.toLowerCase();qstr=qstr.toLowerCase();var ti=0,score=0,prev=-1,marks=[],first=true;for(var qi=0;qi<qstr.length;qi++){var c=qstr[qi];var found=-1;for(;ti<text.length;ti++){if(text[ti]===c){found=ti;break;}}if(found<0) return null;var s=1;if(found===prev+1) s+=5;if(first&&found===0) s+=8;if(found>0&&/[\s\-_\\/.]/.test(text[found-1])) s+=6;score+=s;prev=found;ti=found+1;first=false;marks.push(found);}score+=text.length>0?(1/(1+marks[0]))*4:0;return {score:score,marks:marks};}
function scoreOf(d,qstr){var a=fuzzy(d.Title,qstr);var b=d.Pinyin?fuzzy(d.Pinyin,qstr):null;var best=null,field='title';if(a&&(!best||a.score>best.score)){best=a;field='title';}if(b&&(!best||b.score>best.score)){best=b;field='pinyin';}if(!best) return null;return {score:best.score,field:field};}
function highlight(title,qstr,field){if(!qstr||field!=='title') return esc(title);var r=fuzzy(title,qstr);if(!r) return esc(title);var idx=r.marks,out='',p=0;for(var i=0;i<idx.length;i++){out+=esc(title.slice(p,idx[i]))+'<mark>'+esc(title[idx[i]])+'</mark>';p=idx[i]+1;}out+=esc(title.slice(p));return out;}
function esc(s){return (s||'').replace(/[&<>]/g,function(c){return {'&':'&amp;','<':'&lt;','>':'&gt;'}[c];});}
function colorOf(t){var m={t_cmd:'#6cb0ff',t_nav:'#aab6c5',t_pkg:'#2dd4bf',t_file:'#fbbf24'};return m[t]||'';}

function render(){
  var sub=topSub();
  var mb=document.getElementById('modeBadge'),mt=document.getElementById('modeText');
  var term;
  if(sub){
    mt.textContent=sub.title||'操作';mb.style.color='';mb.style.background='rgba(255,255,255,.06)';
    term=q;
  }else{
    var raw=q;mode=null;term=raw;
    if(raw.length&&PREFIX[raw[0]]){mode=PREFIX[raw[0]];term=raw.slice(1).trimStart();}
    if(mode&&CAT[mode]){mt.textContent=CAT[mode].name;mb.style.color=colorOf('t_'+mode);mb.style.background='rgba(255,255,255,.06)';}
    else{mt.textContent='全部';mb.style.color='';mb.style.background='';}
  }

  var src=sub?sub.items:((mode==='file')?(window.__pm._files||[]):(window.__pm._static||[]));
  var pool=[];
  for(var i=0;i<src.length;i++){var d=src[i];if(sub||!mode||mode==='file'||d.Type===mode) pool.push(d);}

  var scored=[];
  if(term){for(var j=0;j<pool.length;j++){var s=scoreOf(pool[j],term);if(s) scored.push({d:pool[j],s:s});}
    scored.sort(function(a,b){return b.s.score-a.s.score||(CAT[a.d.Type]?CAT[a.d.Type].order:99)-(CAT[b.d.Type]?CAT[b.d.Type].order:99);});}
  else{for(var k=0;k<pool.length;k++){var dd=pool[k];scored.push({d:dd,s:{score:isRecent(dd)?100:10,field:'title'}});}
    scored.sort(function(a,b){return (isRecent(b.d)?1:0)-(isRecent(a.d)?1:0)||(CAT[a.d.Type]?CAT[a.d.Type].order:99)-(CAT[b.d.Type]?CAT[b.d.Type].order:99);});}

  var grpName=sub?(sub.title||'操作'):null;
  var groups={};
  for(var m=0;m<scored.length;m++){var key=sub?grpName:((!term&&isRecent(scored[m].d))?'recent':scored[m].d.Type);(groups[key]=groups[key]||[]).push(scored[m]);}
  var order=sub?[grpName]:(!term?['recent','cmd','nav','pkg','file']:['cmd','nav','pkg','file']);
  var keys=Object.keys(groups).sort(function(a,b){var ia=order.indexOf(a),ib=order.indexOf(b);return (ia<0?99:ia)-(ib<0?99:ib);});

  flat=[];var html='';
  if(!sub&&mode==='file'&&window.__pm._loading&&(!window.__pm._files||window.__pm._files.length===0)){
    html='<div class=\'searching\'><span class=\'sp\'></span>正在搜索文件索引…</div>';
  }else if(scored.length===0){
    html='<div class=\'empty\'><div class=\'big\'>未找到 \''+esc(term)+'\'</div>'+(sub?'换个关键词试试':'试试拼音首字母或英文关键词')+'</div>';
  }else{
    for(var gi=0;gi<keys.length;gi++){
      var kk=keys[gi];
      var meta=sub?{name:sub.title||'操作'}:(kk==='recent'?{name:'最近使用'}:CAT[kk]);
      var items=groups[kk];if(!items||!items.length) continue;
      var icKey=sub?'cmd':(kk==='recent'?'cmd':kk);
      html+='<div class=\'group\'><div class=\'group-head\'>'+(IC[icKey]||'')+'<span>'+esc(meta.name)+'</span><span class=\'cnt\'>'+items.length+'</span></div>';
      for(var ii=0;ii<items.length;ii++){var d=items[ii].d,s=items[ii].s;var idx=flat.length;flat.push(d);
        var cls='item'+(idx===sel?' sel':'');
        var tagsHtml='';
        if(d.Tags){for(var ti=0;ti<d.Tags.length;ti++){var tg=d.Tags[ti];var cc=tg.Kind.indexOf('new')>=0?'badge-new':tg.Kind.indexOf('warn')>=0?'badge-warn':tg.Kind.indexOf('ahead')>=0?'badge-ahead':'badge-st';tagsHtml+='<span class=\'tag '+cc+'\'>'+esc(tg.Text)+'</span>';}}
        if(d.Hint) tagsHtml+='<span class=\'hk\'>'+esc(d.Hint)+'</span>';
        var ic=sub?IC.cmd:(IC[d.Type]||'');
        var ccl=sub?'c-cmd':(CAT[d.Type]?CAT[d.Type].cls:'c-cmd');
        html+='<div class=\''+cls+'\' data-i=\''+idx+'\'><div class=\'ic '+ccl+'\'>'+ic+'</div><div class=\'body\'><div class=\'title\'>'+highlight(d.Title,term,s.field)+'</div>'+(d.Subtitle?'<div class=\'sub\'>'+esc(d.Subtitle)+'</div>':'')+'</div><div class=\'tags\'>'+tagsHtml+'</div></div>';
      }
      html+='</div>';
    }
  }

  var resultsEl=document.getElementById('results');
  resultsEl.innerHTML=html;
  resultsEl.scrollTop=0;
  document.getElementById('footCount').textContent=scored.length;
  var hintBase=sub?'Enter/Tab 执行 · Esc 返回上层':'↑↓ 选择 · Enter 执行 · Tab 打开详情 · Esc 关闭';
  document.getElementById('footHint').textContent=flat[sel]?('Enter 执行：'+flat[sel].Title):hintBase;
  var nodes=resultsEl.querySelectorAll('.item');
  for(var ni=0;ni<nodes.length;ni++){(function(el){var i=+el.dataset.i;el.addEventListener('mouseenter',function(){sel=i;paint();});el.addEventListener('click',function(){sel=i;paint();if(flat[sel]) execute(flat[sel]);});})(nodes[ni]);}
  paint();
}

function paint(){
  var nodes=document.querySelectorAll('#results .item');
  for(var i=0;i<nodes.length;i++){nodes[i].classList.toggle('sel',+nodes[i].dataset.i===sel);}
  var cur=document.querySelector('#results .item.sel');
  if(cur){try{cur.scrollIntoView({block:'nearest'});}catch(e){var rs=document.getElementById('results');rs.scrollTop=cur.offsetTop-rs.clientHeight/2;}}
}

function execute(d){if(!d) return;pushRecent(d);window.__pm.post('execute',{id:d.Id});}
function executeDefault(d){if(!d) return;pushRecent(d);window.__pm.post('execute-default',{id:d.Id});}

window.__pm={
  _static:[],_files:[],_loading:false,
  setCandidates:function(a){this._static=a||[];render();},
  setFileResults:function(a){this._files=a||[];this._loading=false;render();},
  clearFileResults:function(){this._files=[];render();},
  setFileLoading:function(b){this._loading=!!b;render();},
  reset:function(){var inp=document.getElementById('q');inp.value='';q='';this._files=[];this._loading=false;subStack=[];sel=0;render();setTimeout(function(){inp.focus();},0);},
  showActions:function(items,title){subStack.push({title:title||'操作',items:items||[]});var inp=document.getElementById('q');inp.value='';q='';sel=0;render();setTimeout(function(){inp.focus();},0);},
  post:function(t,o){try{if(window.chrome&&window.chrome.webview) window.chrome.webview.postMessage(Object.assign({type:t},o||{}));}catch(e){}}
};

var input=document.getElementById('q');
input.addEventListener('input',function(e){q=e.target.value;sel=0;if(!topSub())window.__pm.post('query',{text:q});render();});
document.addEventListener('keydown',function(e){
  if(e.key==='Escape'){if(subStack.length){subStack.pop();input.value='';q='';sel=0;render();setTimeout(function(){input.focus();},0);e.preventDefault();return;}if(q){q='';input.value='';sel=0;window.__pm.post('query',{text:''});render();}else window.__pm.post('close');e.preventDefault();}
  else if(e.key==='ArrowDown'){e.preventDefault();sel=Math.min(sel+1,Math.max(flat.length-1,0));paint();updateHint();}
  else if(e.key==='ArrowUp'){e.preventDefault();sel=Math.max(sel-1,0);paint();updateHint();}
  else if(e.key==='Enter'){e.preventDefault();if(flat[sel]){if(topSub()&&flat[sel].Type==='pkg-param') executeDefault(flat[sel]);else execute(flat[sel]);}}
  else if(e.key==='Tab'){e.preventDefault();if(topSub()){if(flat[sel]) execute(flat[sel]);}else if(flat[sel]){var d=flat[sel];if(d.Type==='pkg'){window.__pm.post('bridge',{id:d.Id,q:''});}else if(d.Type==='file'){var qq=(q&&q.charAt(0)==='/')?q.slice(1):q;window.__pm.post('bridge',{id:d.Id,q:qq||''});}}}
});
function updateHint(){var sub=topSub();var base=sub?'Enter/Tab 执行 · Esc 返回上层':'↑↓ 选择 · Enter 执行 · Tab 打开详情 · Esc 关闭';document.getElementById('footHint').textContent=flat[sel]?('Enter 执行：'+flat[sel].Title):base;}
render();input.focus();
})();
</script>
</body>
</html>";
        }
    }
}
