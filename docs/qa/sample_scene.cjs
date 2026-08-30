const sharp = require('C:/Users/abluy/AppData/Roaming/npm/node_modules/clawdbot/node_modules/sharp');
const REF='ref_reference.webp', CUR='scene_verify.png';
async function load(p){const {data,info}=await sharp(p).raw().toBuffer({resolveWithObject:true});return {w:info.width,h:info.height,ch:info.channels,data};}
function px(img,x,y){const i=(y*img.w+x)*img.ch;return [img.data[i],img.data[i+1],img.data[i+2]];}
function classify(r,g,b){
  if(b>r+20&&b>g+10){ if(b>140&&g>110)return 'BLUE'; if(r>90&&g>130)return 'LIGHT'; return 'SKY'; }
  if(r>g&&g>b&&r>90&&b<110&&r-b>30)return 'BROWN';
  if(Math.abs(r-g)<25&&Math.abs(g-b)<25&&r>60)return 'GRAY';
  if(r<70&&g<70&&b<80)return 'DARK';
  return 'OTHER';
}
(async()=>{
function colProfile(img,xf){const x=Math.round(img.w*xf);const rows=[];for(let f=0;f<=1.0001;f+=0.02)rows.push({f,rgb:px(img,x,Math.round(img.h*f))});return rows;}
console.log('=== SKY GRADIENT (x=8% col) ===');
for(const [name,img] of [['REF',ref],['CUR',cur]]){console.log('---',name,'---');colProfile(img,0.08).slice(0,22).forEach(r=>console.log(r.f.toFixed(2).padStart(5),JSON.stringify(r.rgb)));}
function buildingExtent(img){const xs=[0.35,0.4,0.45,0.5,0.55,0.6];let top=1,bot=0;for(const xf of xs){const x=Math.round(img.w*xf);for(let y=0;y<img.h;y++){const cl=classify(...px(img,x,y));if(cl==='GRAY'||cl==='DARK'){const f=y/img.h;if(f<top)top=f;if(f>bot)bot=f;}}}return {top,bot,height:bot-top};}
console.log('=== BUILDING EXTENT ===');
for(const [name,img] of [['REF',ref],['CUR',cur]]){const e=buildingExtent(img);console.log(name,'top',e.top.toFixed(3),'bot',e.bot.toFixed(3),'h',e.height.toFixed(3));}
function coverage(img){const counts={};let tot=0;for(let y=0;y<img.h;y+=2)for(let x=0;x<img.w;x+=2){const c=classify(...px(img,x,y));counts[c]=(counts[c]||0)+1;tot++;}const out={};for(const k in counts)out[k]=(counts[k]/tot*100).toFixed(2)+'%';return out;}
console.log('=== COVERAGE ===');
for(const [name,img] of [['REF',ref],['CUR',cur]])console.log(name,JSON.stringify(coverage(img)));
function brownBounds(img){let minx=1,maxx=0,miny=1,maxy=0,cnt=0;for(let y=0;y<img.h;y+=2)for(let x=0;x<img.w;x+=2){if(classify(...px(img,x,y))==='BROWN'){cnt++;const fx=x/img.w,fy=y/img.h;if(fx<minx)minx=fx;if(fx>maxx)maxx=fx;if(fy<miny)miny=fy;if(fy>maxy)maxy=fy;}}return {minx,maxx,miny,maxy,cnt};}
console.log('=== BROWN BOXES BOUNDS ===');
for(const [name,img] of [['REF',ref],['CUR',cur]]){const b=brownBounds(img);console.log(name,'x['+b.minx.toFixed(2)+'-'+b.maxx.toFixed(2)+'] y['+b.miny.toFixed(2)+'-'+b.maxy.toFixed(2)+'] cnt',b.cnt);}
function brightStats(img){const vals=[];for(let y=Math.round(img.h*0.3);y<img.h*0.95;y+=3)for(let x=0;x<img.w;x+=3){const [r,g,b]=px(img,x,y);vals.push(0.299*r+0.587*g+0.114*b);}const mean=vals.reduce((a,c)=>a+c,0)/vals.length;const sd=Math.sqrt(vals.reduce((a,c)=>a+(c-mean)*(c-mean),0)/vals.length);return {mean,sd};}
console.log('=== BRIGHTNESS (y30-95%) ===');
for(const [name,img] of [['REF',ref],['CUR',cur]]){const s=brightStats(img);console.log(name,'mean',s.mean.toFixed(1),'sd',s.sd.toFixed(1));}
function groundSamples(img){const pts=[['groundL',0.15,0.85],['groundC',0.5,0.85],['groundR',0.85,0.85],['nearbase',0.35,0.72],['far',0.5,0.42]];return pts.map(([n,xf,yf])=>[n,px(img,Math.round(img.w*xf),Math.round(img.h*yf))]);}
console.log('=== GROUND SHADOW SAMPLES ===');
for(const [name,img] of [['REF',ref],['CUR',cur]])console.log(name,JSON.stringify(groundSamples(img)));
