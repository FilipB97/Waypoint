namespace RdpManager
{
    /// <summary>
    /// Strona pulpitu renderowana w WebView2. Ładowana RAZ; dane i tokeny motywu przychodzą wiadomością
    /// (<c>PostWebMessageAsJson</c>), więc odświeżenie po sondzie osiągalności albo zmianie motywu nie
    /// przeładowuje strony i nie miga.
    ///
    /// DLACZEGO WEBVIEW, A NIE WPF: pulpit to czysta prezentacja — dane płyną w jedną stronę, nie ma
    /// wejścia, przeciągania ani okien dialogowych. To jedyny ekran, na którym warstwa webowa daje
    /// przewagę (układ i wykresy) bez kosztu interopu. Reszta aplikacji zostaje w WPF, bo hostuje
    /// kontrolki z własnym HWND.
    ///
    /// DECYZJE O WYKRESACH — podjęte NA PODSTAWIE POMIARU, nie gustu. Palety przepuszczone przez
    /// walidator (odległość barwna w OKLab, także w symulacji zaburzeń widzenia barw):
    ///
    /// • Barwy protokołów (#7BA6FF / #4BD6A0 / #F0B45F / #48C6CF / #C0C3CC) OBLAŁY test palety
    ///   kategorycznej: Telnet ma chromę 0,013 (czyta się jako szarość), a para Telnet↔REST ma ΔE 2,2
    ///   w symulacji protanopii i 11,9 przy widzeniu prawidłowym — czyli nie do rozróżnienia nawet bez
    ///   wady wzroku. Dlatego słupki protokołów mają JEDNĄ barwę (długość niesie wielkość), a barwa
    ///   protokołu wraca jako mała kropka przy nazwie, gdzie pełni swoją prawdziwą rolę: tożsamości.
    ///
    /// • Dostępność NIE JEST WYKRESEM. Trójka statusów oblała próg widzenia prawidłowego w motywie
    ///   jasnym (Offline↔Idle ΔE 13,0), a tego progu nie usprawiedliwia etykietowanie. Zamiast malować
    ///   trzy segmenty barwami nie do odróżnienia, pokazujemy liczbę główną i trzy wiersze z KSZTAŁTAMI
    ///   statusu (dysk / pierścień / kreska) — tymi samymi, co w liście serwerów i na kartach.
    ///
    /// • Opóźnienie i tygodniowa aktywność to pojedyncze serie, więc nie ma palety kategorycznej do
    ///   zwalidowania: jedna barwa akcentu, a tożsamość niesie tytuł karty.
    ///
    /// Tokeny motywu przychodzą Z ŻYWEJ PALETY WPF, nie są tu wpisane na sztywno — dzięki temu presety
    /// i własny kolor akcentu działają na pulpicie tak samo jak w reszcie okna.
    /// </summary>
    internal static class DashboardHtml
    {
        internal static string Page => @"<!doctype html><html><head><meta charset=""utf-8"">
<style>
*{box-sizing:border-box}
html,body{margin:0;height:100%}
body{background:transparent;color:var(--prim);
  font:13px/1.45 'Segoe UI',system-ui,sans-serif;-webkit-font-smoothing:antialiased;
  padding:2px 2px 24px;overflow-x:hidden}
.kpis{display:flex;flex-wrap:wrap;gap:0;margin:4px 0 20px}
.kpi{padding-right:28px;margin-right:28px;border-right:1px solid var(--border)}
.kpi:last-child{border-right:0;padding-right:0;margin-right:0}
.kpi .v{font-size:26px;font-weight:700;line-height:1.15;font-variant-numeric:tabular-nums}
.kpi .u{font-size:15px;font-weight:600;color:var(--ter)}
.kpi .l{font-size:11px;color:var(--sec);margin-top:4px}
.grid{display:grid;grid-template-columns:1.55fr 1fr;gap:18px;max-width:1500px}
@media (max-width:900px){.grid{grid-template-columns:1fr}}
.card{border:1px solid var(--border);border-radius:14px;padding:16px 18px 12px;background:var(--panel)}
.card h3{margin:0 0 12px;font-size:13px;font-weight:600;display:flex;align-items:baseline;gap:10px}
.card h3 em{margin-left:auto;font-style:normal;font-size:11.5px;color:var(--ter);
  font-family:Consolas,monospace;font-variant-numeric:tabular-nums}
svg{display:block;width:100%;height:auto;overflow:visible}
.ax{font:10px Consolas,monospace;fill:var(--ter)}
.hero{font-size:34px;font-weight:700;line-height:1;font-variant-numeric:tabular-nums;margin:2px 0 14px}
.hero small{font-size:14px;font-weight:600;color:var(--ter)}
.srow{display:grid;grid-template-columns:14px 1fr auto;gap:10px;align-items:center;padding:5px 0;font-size:12.5px}
.srow b{font-weight:600;font-variant-numeric:tabular-nums;font-family:Consolas,monospace}
.srow span{color:var(--sec)}
.plist{display:flex;flex-direction:column;gap:9px;padding-top:2px}
.prow{display:grid;grid-template-columns:88px 1fr 34px;gap:10px;align-items:center;font-size:12.5px}
.pname{display:flex;align-items:center;gap:7px;color:var(--sec);overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
.pdot{width:8px;height:8px;border-radius:50%;flex:none}
.ptrack{display:block;height:10px;border-radius:5px;background:var(--track);overflow:hidden}
.pbar{display:block;height:100%;border-radius:5px;background:var(--accent);min-width:4px}
.pval{text-align:right;font-family:Consolas,monospace;font-variant-numeric:tabular-nums;color:var(--ter)}
.tip{position:fixed;pointer-events:none;opacity:0;transition:opacity .09s;z-index:9;
  background:var(--panel);border:1px solid var(--border);border-radius:8px;padding:6px 9px;
  font-size:11.5px;box-shadow:0 6px 20px rgba(0,0,0,.35);white-space:nowrap}
.tip b{font-family:Consolas,monospace;font-variant-numeric:tabular-nums}
details{margin-top:18px;max-width:1500px}
summary{cursor:pointer;font-size:11.5px;color:var(--ter);padding:6px 0}
table{border-collapse:collapse;font-size:11.5px;margin-top:6px}
th,td{border:1px solid var(--border);padding:4px 9px;text-align:right}
th:first-child,td:first-child{text-align:left}
th{color:var(--sec);font-weight:600}
td{font-family:Consolas,monospace;font-variant-numeric:tabular-nums;color:var(--ter)}
.empty{color:var(--ter);font-size:12px;padding:18px 0}
</style></head><body>
<div class=""kpis"" id=""kpis""></div>
<div class=""grid"" id=""grid""></div>
<details id=""tbl""></details>
<div class=""tip"" id=""tip""></div>
<script>
'use strict';
var T={}, D=null, tip=document.getElementById('tip');
function esc(s){return String(s).replace(/[&<>]/g,function(c){return{'&':'&amp;','<':'&lt;','>':'&gt;'}[c]})}
function px(n){return Math.round(n*100)/100}

function showTip(html,x,y){tip.innerHTML=html;tip.style.opacity=1;
  var r=tip.getBoundingClientRect();
  tip.style.left=Math.min(Math.max(6,x-r.width/2),innerWidth-r.width-6)+'px';
  tip.style.top=Math.max(6,y-r.height-12)+'px';}
function hideTip(){tip.style.opacity=0}

/* ── Opóźnienie: pojedyncza seria, więc bez legendy — tytuł karty ją nazywa.
      Krzyżyk i etykieta pod kursorem, bo wykres liniowy bez odczytu punktu to sama sylwetka. ── */
function latency(el,data){
  var w=560,h=150,l=34,r=8,t=10,b=22, iw=w-l-r, ih=h-t-b;
  if(!data.length){el.innerHTML='<div class=""empty"">'+esc(T.noLatency)+'</div>';return}
  var max=Math.max.apply(null,data), min=Math.min.apply(null,data);
  if(max===min){max=max+1;min=Math.max(0,min-1)}
  var pad=(max-min)*0.15; max=Math.ceil(max+pad); min=Math.floor(Math.max(0,min-pad));
  var X=function(i){return l+(data.length<2?iw:iw*i/(data.length-1))},
      Y=function(v){return t+ih-(v-min)/(max-min)*ih};
  var pts=data.map(function(v,i){return px(X(i))+' '+px(Y(v))}).join(' L ');
  var mid=Math.round((max+min)/2);
  var g='';
  [max,mid,min].forEach(function(v){var y=px(Y(v));
    g+='<line x1=""'+l+'"" y1=""'+y+'"" x2=""'+(w-r)+'"" y2=""'+y+'"" stroke=""'+T.border+'"" stroke-width=""1""/>'+
       '<text class=""ax"" x=""'+(l-6)+'"" y=""'+(y+3)+'"" text-anchor=""end"">'+v+'</text>'})
  el.innerHTML='<svg viewBox=""0 0 '+w+' '+h+'"" role=""img"" aria-label=""'+esc(T.aria.lat)+'"">'+g+
    '<path d=""M '+pts+' L '+px(X(data.length-1))+' '+(t+ih)+' L '+px(X(0))+' '+(t+ih)+' Z"" fill=""'+T.accentSoft+'""/>'+
    '<path d=""M '+pts+'"" fill=""none"" stroke=""'+T.accent+'"" stroke-width=""2"" stroke-linejoin=""round"" stroke-linecap=""round""/>'+
    '<circle cx=""'+px(X(data.length-1))+'"" cy=""'+px(Y(data[data.length-1]))+'"" r=""4"" fill=""'+T.accent+'"" stroke=""'+T.panel+'"" stroke-width=""2""/>'+
    '<line id=""cx"" y1=""'+t+'"" y2=""'+(t+ih)+'"" stroke=""'+T.accent+'"" stroke-width=""1"" opacity=""0""/>'+
    '<rect x=""'+l+'"" y=""'+t+'"" width=""'+iw+'"" height=""'+ih+'"" fill=""transparent"" id=""hit""/></svg>';
  var svg=el.firstChild, cx=svg.querySelector('#cx'), hit=svg.querySelector('#hit');
  hit.addEventListener('mousemove',function(e){
    var bb=svg.getBoundingClientRect(), sx=(e.clientX-bb.left)/bb.width*w;
    var i=Math.round((sx-l)/iw*(data.length-1)); i=Math.max(0,Math.min(data.length-1,i));
    cx.setAttribute('x1',px(X(i)));cx.setAttribute('x2',px(X(i)));cx.setAttribute('opacity','.55');
    showTip('<b>'+data[i]+' ms</b>',e.clientX,e.clientY)});
  hit.addEventListener('mouseleave',function(){cx.setAttribute('opacity','0');hideTip()});
}

/* ── Aktywność w tygodniu: jedna seria, słupki. Wartości w tokenie tekstu, nie w barwie serii. ── */
function weekday(el,data,names){
  var w=560,h=150,l=8,r=8,t=16,b=22, iw=w-l-r, ih=h-t-b;
  if(!data.length||!data.some(function(v){return v>0})){el.innerHTML='<div class=""empty"">'+esc(T.noData)+'</div>';return}
  var max=Math.max.apply(null,data)||1, bw=iw/data.length, pad=bw*0.26;
  var s='';
  data.forEach(function(v,i){
    var bh=Math.max(v>0?3:0,ih*v/max), x=px(l+i*bw+pad/2), y=px(t+ih-bh);
    s+='<rect class=""bar"" data-i=""'+i+'"" x=""'+x+'"" y=""'+y+'"" width=""'+px(bw-pad)+'"" height=""'+px(bh)+
       '"" rx=""4"" fill=""'+T.accent+'"" opacity=""'+(0.45+0.55*v/max)+'""/>'+
       '<text class=""ax"" x=""'+px(x+(bw-pad)/2)+'"" y=""'+px(y-5)+'"" text-anchor=""middle"">'+v+'</text>'+
       '<text class=""ax"" x=""'+px(x+(bw-pad)/2)+'"" y=""'+(h-6)+'"" text-anchor=""middle"">'+esc(names[i]||'')+'</text>'});
  el.innerHTML='<svg viewBox=""0 0 '+w+' '+h+'"" role=""img"" aria-label=""'+esc(T.aria.week)+'"">'+s+'</svg>';
  Array.prototype.forEach.call(el.querySelectorAll('.bar'),function(b){
    b.addEventListener('mousemove',function(e){var i=+b.dataset.i;
      showTip(esc(names[i])+' · <b>'+data[i]+'</b>',e.clientX,e.clientY)});
    b.addEventListener('mouseleave',hideTip)});
}

/* ── Dostępność: NIE wykres. Liczba główna + kształty statusu (patrz komentarz w DashboardHtml.cs). ── */
function availability(el,m){
  function glyph(kind,color){
    if(kind==='disc') return '<svg width=""14"" height=""14"" viewBox=""0 0 14 14""><circle cx=""7"" cy=""7"" r=""4"" fill=""'+color+'""/></svg>';
    if(kind==='ring') return '<svg width=""14"" height=""14"" viewBox=""0 0 14 14""><circle cx=""7"" cy=""7"" r=""4.4"" fill=""none"" stroke=""'+color+'"" stroke-width=""2.5""/></svg>';
    return '<svg width=""14"" height=""14"" viewBox=""0 0 14 14""><rect x=""2"" y=""6"" width=""10"" height=""2"" rx=""1"" fill=""'+color+'""/></svg>';
  }
  var rows=[['disc',T.online,m.online,T.cOnline],['ring',T.idle,m.idle,T.cIdle],['bar',T.offline,m.offline,T.cOffline]];
  el.innerHTML='<div class=""hero"">'+m.online+'<small> / '+m.servers+'</small></div>'+
    rows.map(function(r){return '<div class=""srow"">'+glyph(r[0],r[3])+'<span>'+esc(r[1])+'</span><b>'+r[2]+'</b></div>'}).join('');
}

/* ── Protokoły: długość = wielkość (jedna barwa), kropka przy nazwie = tożsamość. ── */
function protocols(el,list){
  if(!list.length){el.innerHTML='<div class=""empty"">'+esc(T.noData)+'</div>';return}
  var max=Math.max.apply(null,list.map(function(p){return p.count}))||1;
  el.innerHTML='<div class=""plist"">'+list.map(function(p){
    return '<div class=""prow"" title=""'+esc(p.name)+': '+p.count+'"">'+
      '<span class=""pname""><i class=""pdot"" style=""background:'+(T.proto[p.colorKey]||T.ter)+'""></i>'+esc(p.name)+'</span>'+
      '<span class=""ptrack""><span class=""pbar"" style=""width:'+px(100*p.count/max)+'%""></span></span>'+
      '<span class=""pval"">'+p.count+'</span></div>'}).join('')+'</div>';
}

function card(title,sub,id){
  return '<div class=""card""><h3>'+esc(title)+(sub?'<em>'+esc(sub)+'</em>':'')+'</h3><div id=""'+id+'""></div></div>';
}

function render(){
  if(!D) return;
  var m=D.model, s=D.strings;
  document.getElementById('kpis').innerHTML=[
    [m.servers,'',s.servers,T.prim],[m.online,'',s.online,T.cOnline],
    [m.openSessions,'',s.sessions,T.prim],
    [m.avgLatency<0?'—':m.avgLatency, m.avgLatency<0?'':' ms', s.avgLatency,T.prim],
    [m.groups,'',s.groups,T.prim]
  ].map(function(k){return '<div class=""kpi""><div class=""v"" style=""color:'+k[3]+'"">'+k[0]+
      (k[1]?'<span class=""u"">'+k[1]+'</span>':'')+'</div><div class=""l"">'+esc(k[2])+'</div></div>'}).join('');

  document.getElementById('grid').innerHTML=
    card(s.latency, m.avgLatency<0?'':s.avg+' '+m.avgLatency+' ms','c1')+
    card(s.availability, m.online+'/'+m.servers,'c2')+
    card(s.week, m.weekday.reduce(function(a,b){return a+b},0).toString(),'c3')+
    card(s.protocols, m.servers.toString(),'c4');

  latency(document.getElementById('c1'), m.latencySeries);
  availability(document.getElementById('c2'), m);
  weekday(document.getElementById('c3'), m.weekday, s.weekdays);
  protocols(document.getElementById('c4'), m.protocols);

  var t='<summary>'+esc(s.tableView)+'</summary><table><thead><tr><th>'+esc(s.metric)+'</th><th>'+esc(s.value)+'</th></tr></thead><tbody>';
  t+='<tr><td>'+esc(s.servers)+'</td><td>'+m.servers+'</td></tr>';
  t+='<tr><td>'+esc(s.online)+'</td><td>'+m.online+'</td></tr>';
  t+='<tr><td>'+esc(s.idle)+'</td><td>'+m.idle+'</td></tr>';
  t+='<tr><td>'+esc(s.offline)+'</td><td>'+m.offline+'</td></tr>';
  m.weekday.forEach(function(v,i){t+='<tr><td>'+esc(s.weekdays[i]||'')+'</td><td>'+v+'</td></tr>'});
  m.protocols.forEach(function(p){t+='<tr><td>'+esc(p.name)+'</td><td>'+p.count+'</td></tr>'});
  document.getElementById('tbl').innerHTML=t+'</tbody></table>';
}

window.chrome.webview.addEventListener('message',function(e){
  D=e.data; T=D.theme; T.aria=D.strings.aria;
  T.noLatency=D.strings.noLatency; T.noData=D.strings.noData;
  T.online=D.strings.online; T.idle=D.strings.idle; T.offline=D.strings.offline;
  var r=document.documentElement.style;
  r.setProperty('--prim',T.prim); r.setProperty('--sec',T.sec); r.setProperty('--ter',T.ter);
  r.setProperty('--border',T.border); r.setProperty('--panel',T.panel);
  r.setProperty('--accent',T.accent); r.setProperty('--track',T.track);
  render();
});
addEventListener('resize',function(){if(D) render()});
</script></body></html>";
    }
}
