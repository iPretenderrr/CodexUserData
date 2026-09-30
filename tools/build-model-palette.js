#!/usr/bin/env node
'use strict';
// Offline palette maintenance only. The application consumes the frozen JSON.
const fs = require('fs');
const path = require('path');
const ROOT = path.resolve(__dirname, '..');
const OUT = path.join(ROOT, '.build', 'palette-design');
const SPEC = { astra:[200,235], sol:[18,58], terra:[115,165], luna:[328,372], gpt5:[242,294] };
const INITIAL_MODELS = {
  astra:['gpt-6.0-astra'],
  sol:['gpt-6.0-sol','gpt-6.1-sol','gpt-5.6-sol'],
  terra:['gpt-5.6-terra'],
  luna:['gpt-6.0-luna','gpt-5.6-luna'],
  gpt5:['gpt-5.3-codex','gpt-5.3-codex-spark','gpt-5.4','gpt-5.5','gpt-5.6','gpt-5.0','gpt-5.1','gpt-5.2']
};
const BACKGROUND = { dark:'#2B3846', light:'#E7EFF7' };
// Nearby consecutive translations protect mixed windows across round boundaries.
const ROUNDS = [[0,0,0],[-.007,-.002,.002],[.002,.004,.003],[.009,.002,-.002]];
// Green on a light background has the narrowest readable color volume. A
// monotonic small lightness shift protects its mixed-round minimum separation.
function roundVectors(family,theme) { return family==='terra' && theme==='light'?[[0,0,0],[.003,0,0],[.006,0,0],[.009,0,0]]:ROUNDS; }
function familyRounds(family) { return ROUNDS.map((q,i)=>({dark:roundVectors(family,'dark')[i].slice(),light:roundVectors(family,'light')[i].slice()})); }
let rngState = 0x20260930;
function random() { rngState = (Math.imul(1664525,rngState)+1013904223)>>>0; return rngState/4294967296; }
function rgb(hex) { return [1,3,5].map(i=>parseInt(hex.slice(i,i+2),16)/255); }
function hex(c) { return '#'+c.map(x=>Math.round(x*255).toString(16).padStart(2,'0')).join('').toUpperCase(); }
function linear(x) { return x<=.04045?x/12.92:Math.pow((x+.055)/1.055,2.4); }
function gamma(x) { return x<=.0031308?12.92*x:1.055*Math.pow(x,1/2.4)-.055; }
function lab(c) {
  const [r,g,b]=c.map(linear), l=Math.cbrt(.4122214708*r+.5363325363*g+.0514459929*b), m=Math.cbrt(.2119034982*r+.6806995451*g+.1073969566*b), s=Math.cbrt(.0883024619*r+.2817188376*g+.6299787005*b);
  return [.2104542553*l+.793617785*m-.0040720468*s,1.9779984951*l-2.428592205*m+.4505937099*s,.0259040371*l+.7827717662*m-.808675766*s];
}
function fromLab([L,a,b]) {
  const l=(L+.3963377774*a+.2158037573*b)**3, m=(L-.1055613458*a-.0638541728*b)**3, s=(L-.0894841775*a-1.291485548*b)**3;
  return [4.0767416621*l-3.3077115913*m+.2309699292*s,-1.2684380046*l+2.6097574011*m-.3413193965*s,-.0041960863*l-.7034186147*m+1.707614701*s].map(gamma);
}
function hue([r,g,b]) {
  const mx=Math.max(r,g,b), mn=Math.min(r,g,b), d=mx-mn;
  if(d<1e-9)return NaN;
  return ((mx===r?(g-b)/d:mx===g?(b-r)/d+2:(r-g)/d+4)*60+360)%360;
}
function hsl(h,s,l) { h=(h%360)/60; const c=(1-Math.abs(2*l-1))*s,x=c*(1-Math.abs(h%2-1)),m=l-c/2; return (h<1?[c,x,0]:h<2?[x,c,0]:h<3?[0,c,x]:h<4?[0,x,c]:h<5?[x,0,c]:[c,0,x]).map(v=>v+m); }
function lum(c) { const v=c.map(linear); return .2126*v[0]+.7152*v[1]+.0722*v[2]; }
function contrast(c,bg) { const a=lum(c),b=lum(bg); return (Math.max(a,b)+.05)/(Math.min(a,b)+.05); }
function distance(a,b) { return Math.hypot(a[0]-b[0],a[1]-b[1],a[2]-b[2]); }
function insideHue(h,range) { if(h<range[0] && range[1]>360)h+=360; return h>=range[0] && h<=range[1]; }
function translated(base,q) { return fromLab(base.map((x,i)=>x+q[i])); }
// Reject out-of-gamut candidates before RGB quantization: clipping cannot hide failures.
function candidate(c,family,theme) {
  const color=hex(c), base=lab(rgb(color)), bg=rgb(BACKGROUND[theme]), variants=[];
  for(const q of roundVectors(family,theme)) {
    const raw=translated(base,q);
    if(raw.some(x=>x<0 || x>1))return null;
    const quant=rgb(hex(raw)), v=lab(quant), chroma=Math.hypot(v[1],v[2]);
    const narrowGreen=family==='terra' && theme==='light';
    if(!insideHue(hue(quant),SPEC[family]) || contrast(quant,bg)<4.52 || chroma<(narrowGreen?.045:.058))return null;
    // Avoid near-white and near-black text, retaining a visibly colored family.
    if(v[0]<(theme==='dark'?.65:narrowGreen?.29:.33) || v[0]>(theme==='dark'?.88:.59))return null;
    variants.push({hex:hex(raw),lab:v});
  }
  if(new Set(variants.map(v=>v.hex)).size!==ROUNDS.length)return null;
  return {hex:color,lab:base,variants};
}
function minDistance(items) { let min=Infinity; for(let i=0;i<items.length;i++)for(let j=0;j<i;j++)min=Math.min(min,distance(items[i],items[j])); return min; }
function paletteScore(items) { return minDistance(items.map(x=>x.lab)); }
function searchDistance(a,b,i,j,family) { return distance(a.lab,b.lab)*(family==='gpt5' && i<5 && j<5?.75:1); }
function searchScore(items,family) { let score=Infinity;for(let i=0;i<items.length;i++)for(let j=0;j<i;j++)score=Math.min(score,searchDistance(items[i],items[j],i,j,family));return score; }
function pairThemes(family,f) {
  // Minimum-cost assignment pairs each dark slot with a similar hue/chroma in
  // the light theme. This preserves the candidate color sets and model indices.
  const features=colors=> {
    const rows=colors.map(c=>{const v=lab(rgb(c));let h=hue(rgb(c));if(family==='luna'&&h<328)h+=360;return [h,Math.hypot(v[1],v[2])];});
    const lo=Math.min(...rows.map(x=>x[1])),span=Math.max(...rows.map(x=>x[1]))-lo;
    return rows.map(x=>[x[0],(x[1]-lo)/span]);
  };
  const dark=features(f.dark),light=features(f.light),width=SPEC[family][1]-SPEC[family][0],memo=new Map();
  function solve(mask) {
    if(mask===1023)return {cost:0,order:[]};
    if(memo.has(mask))return memo.get(mask);
    let row=0;for(let x=mask;x;x>>=1)row+=x&1;
    let best={cost:Infinity};
    for(let j=0;j<10;j++)if(!(mask&(1<<j))) {
      const rest=solve(mask|(1<<j)),cost=rest.cost+4*((dark[row][0]-light[j][0])/width)**2+.25*(dark[row][1]-light[j][1])**2;
      if(cost<best.cost)best={cost,order:[j,...rest.order]};
    }
    memo.set(mask,best);return best;
  }
  f.light=solve(0).order.map(i=>f.light[i]);
}
function optimize(family,theme) {
  const priorityKey=family==='gpt5' && theme==='light'?'gpt5-light':family;
  // Give each table an independent reproducible stream, so targeted maintenance
  // produces the same result as rebuilding all tables in the documented order.
  rngState=0x20260930;
  const stream=Object.keys(SPEC).indexOf(family)*2+(theme==='light'?1:0);
  for(let i=0;i<stream*480036;i++)random();
  const candidates=[], seen=new Set(), range=SPEC[family];
  for(let i=0;i<160000;i++) {
    const c=hsl(range[0]+random()*(range[1]-range[0]),family==='terra' && theme==='light'?.25+random()*.75:.38+random()*.62,theme==='dark'?.48+random()*.38:family==='terra'?.08+random()*.51:.19+random()*.4);
    const v=candidate(c,family,theme);
    if(v && !seen.has(v.hex)) {seen.add(v.hex);candidates.push(v);}
  }
  if(candidates.length<10)throw Error('Not enough candidates: '+family+' '+theme);
  let best=[], bestScore=0;
  // Deterministic farthest-point restarts plus coordinate improvement approximate
  // max-min packing. This is not a proof of the global optimum.
  for(let restart=0;restart<36;restart++) {
    const chosen=[candidates[Math.floor(random()*candidates.length)]], near=candidates.map(c=>distance(c.lab,chosen[0].lab));
    while(chosen.length<10) {
      let idx=0;for(let i=1;i<near.length;i++)if(near[i]>near[idx])idx=i;
      chosen.push(candidates[idx]);
      for(let i=0;i<near.length;i++)near[i]=Math.min(near[i],distance(candidates[i].lab,candidates[idx].lab));
    }
    for(let pass=0;pass<4;pass++) {
      let changed=false;
      for(let pos=0;pos<10;pos++) {
        let nearest=Infinity;for(let j=0;j<10;j++)if(j!==pos)nearest=Math.min(nearest,searchDistance(chosen[pos],chosen[j],pos,j,priorityKey));
        let replacement=chosen[pos];
        for(const c of candidates) {
          let d=Infinity;for(let j=0;j<10;j++)if(j!==pos) {d=Math.min(d,searchDistance(c,chosen[j],pos,j,priorityKey));if(d<=nearest)break;}
          if(d>nearest+1e-10) {nearest=d;replacement=c;}
        }
        if(replacement!==chosen[pos]) {chosen[pos]=replacement;changed=true;}
      }
      if(!changed)break;
    }
    const score=searchScore(chosen,priorityKey);
    if(score>bestScore) {best=chosen.slice();bestScore=score;}
  }
  // Front-load the most separated slots; this prioritizes Sol 0/1 and GPT5 0..4.
  const ordered=priorityKey==='gpt5'?best.slice(0,5):[best.reduce((a,b)=>a.lab[0]>b.lab[0]?a:b)], remaining=best.filter(x=>!ordered.includes(x));
  while(remaining.length) {
    let idx=0,score=-1;for(let i=0;i<remaining.length;i++) {const s=Math.min(...ordered.map(x=>distance(x.lab,remaining[i].lab)));if(s>score){score=s;idx=i;}}
    ordered.push(remaining.splice(idx,1)[0]);
  }
  console.log(family,theme,'candidates',candidates.length,'base',paletteScore(ordered).toFixed(5),'front5',paletteScore(ordered.slice(0,5)).toFixed(5));
  return ordered.map(x=>x.hex);
}
function measure(palette) {
  const report={algorithm:'OKLab Euclidean on RGB8 output; WCAG relative luminance',backgrounds:BACKGROUND,targets:{baseMin:.06,slidingMin:.05,gpt5FirstFiveMin:.08,contrastMin:4.5},families:{},limitations:['Search is deterministic heuristic optimization, not a certified mathematical upper bound.','Perceptual color differences vary with display and color vision; numerical separation does not replace labels.']};
  for(const [name,f] of Object.entries(palette.families)) {
    report.families[name]={};
    for(const theme of ['dark','light']) {
      const all=[], rawOutside=[], hues=[], contrasts=[];
      f.rounds.forEach((q,round)=>f[theme].forEach((color,slot)=>{
        const raw=translated(lab(rgb(color)),q[theme]);
        if(raw.some(x=>x<0||x>1))rawOutside.push([round,slot]);
        const h=hex(raw),c=rgb(h);all.push({hex:h,lab:lab(c)});hues.push(hue(c));contrasts.push(contrast(c,rgb(BACKGROUND[theme])));
      }));
      const boundaries=[];for(let round=0;round<f.rounds.length-1;round++) {
        const windows=[];for(let start=1;start<10;start++)windows.push(minDistance(all.slice(round*10+start,round*10+start+10).map(x=>x.lab)));
        boundaries.push({from:round,to:round+1,windows,min:Math.min(...windows)});
      }
      const roundMinimums=f.rounds.map((q,i)=>minDistance(all.slice(i*10,i*10+10).map(x=>x.lab)));
      const m={baseMin:roundMinimums[0],firstFiveMin:minDistance(all.slice(0,5).map(x=>x.lab)),slot01:distance(all[0].lab,all[1].lab),roundMinimums,boundaries,slidingMin:Math.min(...boundaries.map(x=>x.min)),contrastMin:Math.min(...contrasts),hueMin:Math.min(...hues.map(x=>name==='luna'&&x<328?x+360:x)),hueMax:Math.max(...hues.map(x=>name==='luna'&&x<328?x+360:x)),outOfGamut:rawOutside,unique:new Set(all.map(x=>x.hex)).size,total:all.length,colors:all.map(x=>x.hex)};
      m.lightnessRange=[Math.min(...all.map(x=>x.lab[0])),Math.max(...all.map(x=>x.lab[0]))];
      m.chromaRange=[Math.min(...all.map(x=>Math.hypot(x.lab[1],x.lab[2]))),Math.max(...all.map(x=>Math.hypot(x.lab[1],x.lab[2])))];
      m.baseTargetMet=m.baseMin>=.06;
      m.gpt5FirstFiveTargetMet=name==='gpt5'?m.firstFiveMin>=.08:null;
      if(m.baseMin<.06 || (name==='gpt5' && m.firstFiveMin<.08) || m.contrastMin<4.5 || m.slidingMin<.05 || Math.min(...roundMinimums)<.05 || m.outOfGamut.length || m.unique!==m.total || all.some(x=>!insideHue(hue(rgb(x.hex)),SPEC[name])))throw Error('Hard validation failed: '+name+' '+theme+' '+JSON.stringify(m));
      report.families[name][theme]=m;
    }
  }
  return report;
}
function preview(p,report) {
  let html='<!doctype html><html lang="zh-CN"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>Model palette · frozen RGB8 verification</title><style>body{margin:0;background:#18232f;color:#e7eff7;font:15px system-ui}main{max-width:1440px;margin:auto;padding:28px}h1{font-size:28px}h2{text-transform:uppercase;font-size:18px;margin-top:30px}.pair{display:grid;grid-template-columns:1fr 1fr;gap:18px}.theme{padding:18px;border-radius:12px}.dark{background:#2B3846}.light{background:#E7EFF7;color:#263548}.row{display:grid;grid-template-columns:repeat(10,minmax(0,1fr));gap:7px;margin:9px 0}.swatch{min-height:77px;font-weight:700;font-size:19px}.bar{height:18px;border-radius:4px;margin:6px 0}.hex{font:10px ui-monospace,monospace}.meta{font-size:12px;opacity:.85}.round{font-size:12px;margin:14px 0 3px}footer{margin-top:28px;font-size:13px;line-height:1.6}@media(max-width:1100px){.pair{grid-template-columns:1fr}.row{gap:4px}.swatch{font-size:16px}.hex{font-size:9px}}</style><main><h1>模型家族色板 · 10 个色位 × 4 轮</h1><p>每轮整体 OKLab 向量平移；所有数字基于最终 RGB8。编号为统一 append-only 模型索引 n，色位 n % 10，轮次 floor(n / 10)。</p>';
  for(const [name,f] of Object.entries(p.families)) {
    html+='<h2>'+name+'</h2><div class="pair">';
    for(const theme of ['dark','light']) {
      const m=report.families[name][theme];html+='<section class="theme '+theme+'"><strong>'+theme+'</strong><div class="meta">基础 ΔE '+m.baseMin.toFixed(4)+' · 跨轮窗口 '+m.slidingMin.toFixed(4)+' · 最低对比 '+m.contrastMin.toFixed(2)+':1</div>';
      f.rounds.forEach((q,r)=>{html+='<div class="round">轮 '+r+' · Δ ['+q[theme].join(', ')+']</div><div class="row">';m.colors.slice(r*10,r*10+10).forEach((c,i)=>{html+='<div class="swatch" style="color:'+c+'">Aa '+(r*10+i)+'<div class="bar" style="background:'+c+'"></div><div class="hex">'+c+'</div></div>';});html+='</div>';});html+='</section>';
    }
    html+='</div>';
  }
  return html+'<footer>限制：启发式搜索不构成全局最优证明。0.06 / 0.05 是优化目标；实际结果见相邻统计。颜色不足以单独表达模型身份，应同时显示文字。家族色相按 HSL 检查，距离采用 OKLab。</footer></main></html>';
}
function main() {
  fs.mkdirSync(OUT,{recursive:true});
  const filename=path.join(ROOT,'model-palette.json');
  const existing=fs.existsSync(filename)?JSON.parse(fs.readFileSync(filename,'utf8')):null;
  let changed=false;
  let palette;
  if(process.argv.includes('--generate')) {
    palette={schema:1,revision:existing?.revision || 2026093001,algorithm:'family-cycle-v1',families:{}};
    for(const name of Object.keys(SPEC)) {
      const models=existing?.families?.[name]?.models || INITIAL_MODELS[name].slice();
      palette.families[name]={dark:optimize(name,'dark'),light:optimize(name,'light'),rounds:familyRounds(name),models};
    }
    changed=true;
  } else palette=existing;
  if(!palette)throw Error('No frozen palette found; use --generate for the initial build.');
  const refine=process.argv.indexOf('--refine');
  if(refine>=0) {
    const [family,theme]=String(process.argv[refine+1]).split(':');
    if(!SPEC[family] || !['dark','light'].includes(theme))throw Error('Use --refine family:dark or family:light');
    palette.families[family][theme]=optimize(family,theme);
    for(const [name,f] of Object.entries(palette.families))f.rounds=familyRounds(name);
    changed=true;
  }
  if(process.argv.includes('--pair'))changed=true;
  if(changed)for(const [name,f] of Object.entries(palette.families))pairThemes(name,f);
  const report=measure(palette);
  // Published bases cannot change through an append-only update. Subsequent
  // design experiments stay in .build instead of rewriting the release file.
  if(changed)fs.writeFileSync(existing?path.join(OUT,'model-palette.candidate.json'):filename,JSON.stringify(palette,null,2)+'\n');
  fs.writeFileSync(path.join(OUT,'measurements.json'),JSON.stringify(report,null,2)+'\n');
  fs.writeFileSync(path.join(OUT,'preview.html'),preview(palette,report));
  for(const [f,info] of Object.entries(report.families))for(const [t,m] of Object.entries(info))console.log(f,t,JSON.stringify({base:m.baseMin,window:m.slidingMin,first5:m.firstFiveMin,contrast:m.contrastMin,unique:m.unique}));
}
if(require.main===module)main();
module.exports={rgb,hex,lab,fromLab,hue,contrast,distance,measure,preview,SPEC,ROUNDS};
