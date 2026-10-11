"use strict";
const labels={asrProvider:"识别引擎",cloudBaseUrl:"ASR 服务地址",cloudEndpoint:"ASR 路径",cloudModel:"ASR 模型",cloudApiKey:"ASR API Key",cloudTimeoutSeconds:"ASR 超时（秒）",cloudConcurrency:"ASR 并发数",cloudLanguage:"识别语言",translateChineseToEnglish:"中文翻译为英文",fileAssociationEnabled:"ILP 文件关联",mcpEnabled:"智能体 MCP 服务",localModelsDirectory:"本地模型目录",selectedLocalModel:"本地模型",detectedLocalModels:"已发现模型",themeMode:"应用主题",applicationFontFamily:"应用字体",applicationFontWeight:"字体字重",skipOpeningPrompts:"跳过打开提示",createProjectOnAudioImport:"导入音频时建立项目",transcriptFontSize:"字幕字号",playerKeyBindings:"播放器快捷键",debugLogging:"调试日志"};
const featureLabels={playback:"播放与跳转",importAudio:"导入音频",importIlp:"导入精听包",authoring:"课程制作",asr:"音频转写",mcp:"智能体接管",export:"课程导出",importProject:"导入工程",dataMaintenance:"数据维护",apiConfiguration:"API 配置操作",updates:"组织更新"};
const payload=JSON.parse(document.getElementById("initial-policy").textContent),schema=JSON.parse(document.getElementById("settings-schema").textContent),features=JSON.parse(document.getElementById("features-schema").textContent),query=new URLSearchParams(location.search);
function tab(name){if(!["devices","policy","updates","telemetry","connection"].includes(name))name="devices";document.querySelectorAll("[data-panel]").forEach(p=>p.hidden=p.dataset.panel!==name);document.querySelectorAll("[data-tab]").forEach(b=>{b.classList.toggle("active",b.dataset.tab===name);b.setAttribute("aria-current",b.dataset.tab===name?"page":"false");});document.getElementById("page-title").textContent=document.querySelector(`[data-tab="${name}"]`).textContent.trim();query.set("view",name);history.replaceState(null,"","?"+query);}
document.querySelectorAll("[data-tab]").forEach(b=>b.addEventListener("click",()=>tab(b.dataset.tab)));tab(query.get("view")||(query.get("scope")&&query.get("scope")!=="all"?"policy":"devices"));
document.querySelectorAll(".scope-picker").forEach(s=>s.addEventListener("change",()=>{query.set("scope",s.value);location.search=query.toString();}));
function filterDevices(){const search=document.getElementById("device-search").value.toLowerCase(),status=document.getElementById("device-status").value;document.querySelectorAll("[data-device]").forEach(r=>r.hidden=!r.dataset.device.toLowerCase().includes(search)||(status!=="all"&&r.dataset.status!==status));}
document.getElementById("device-search").addEventListener("input",filterDevices);document.getElementById("device-status").addEventListener("change",filterDevices);document.querySelectorAll("form[data-confirm]").forEach(f=>f.addEventListener("submit",e=>{if(!confirm(f.dataset.confirm))e.preventDefault();}));
function select(options){const s=document.createElement("select");for(const[value,label]of options){const o=document.createElement("option");o.value=value;o.textContent=label;s.append(o);}return s;}
document.querySelectorAll("[data-option]").forEach(s=>s.value=Object.hasOwn(payload,s.dataset.option)?String(payload[s.dataset.option]):"inherit");
const featureMode=document.getElementById("features-mode");featureMode.value=Object.hasOwn(payload,"disabledFeatures")?"override":"inherit";
for(const key of features){const label=document.createElement("label"),input=document.createElement("input");input.type="checkbox";input.dataset.feature=key;input.checked=!(payload.disabledFeatures||[]).includes(key);label.append(input,document.createTextNode(featureLabels[key]||key));document.getElementById("feature-fields").append(label);}
function featureState(){document.querySelectorAll("[data-feature]").forEach(c=>c.disabled=featureMode.value==="inherit");}featureMode.addEventListener("change",featureState);featureState();
const fields=[];
for(const[key,type]of Object.entries(schema)){
 const row=document.createElement("div");row.className="setting-row";row.dataset.search=(key+" "+(labels[key]||key)).toLowerCase();
 const name=document.createElement("label");name.className="setting-name";name.textContent=labels[key]||key;const small=document.createElement("small");small.textContent=key;name.append(small);
 const mode=select([["inherit","继承 / 用户可改"],["lock","仅锁定当前值"],["enforce","指定组织值"]]);mode.setAttribute("aria-label",(labels[key]||key)+"管理方式");
 const saved=payload.settings?.[key];mode.value=saved!==undefined?"enforce":(payload.lockedSettings||[]).includes(key)?"lock":"inherit";
 let input;
 if(type==="boolean")input=select([["true","开启"],["false","关闭"]]);
 else if(key==="themeMode")input=select([["system","跟随系统"],["light","浅色"],["dark","深色"]]);
 else if(key==="asrProvider")input=select([["cloud","云端 ASR"],["local","本地 ASR"]]);
 else{input=document.createElement(type==="array"||type==="object"?"textarea":"input");if(type==="number")input.type="number";else if(key==="cloudApiKey"){input.type="password";input.autocomplete="new-password";input.placeholder=saved==="__KEEP_SECRET__"?"已保存，留空保留":"输入组织密钥";}}
 input.id="setting-"+key;name.htmlFor=input.id;
 input.value=key==="cloudApiKey"&&saved==="__KEEP_SECRET__"?"":saved!==undefined?(typeof saved==="object"?JSON.stringify(saved,null,2):String(saved)):(type==="boolean"?"false":type==="array"?"[]":type==="object"?"{}":input.tagName==="SELECT"?input.value:"");
 function state(){input.disabled=mode.value!=="enforce";}mode.addEventListener("change",state);state();row.append(name,mode,input);document.getElementById("setting-fields").append(row);fields.push({key,type,mode,input,saved});
}
document.getElementById("setting-search").addEventListener("input",e=>document.querySelectorAll(".setting-row").forEach(r=>r.hidden=!r.dataset.search.includes(e.target.value.toLowerCase())));
document.getElementById("policy-form").addEventListener("submit",e=>{
 try{
  const result={settings:{},lockedSettings:[]};document.querySelectorAll("[data-option]").forEach(s=>{if(s.value!=="inherit")result[s.dataset.option]=s.value==="true";});
  if(featureMode.value!=="inherit")result.disabledFeatures=Array.from(document.querySelectorAll("[data-feature]")).filter(c=>!c.checked).map(c=>c.dataset.feature);
  for(const{key,type,mode,input,saved}of fields){
   if(mode.value==="lock")result.lockedSettings.push(key);if(mode.value!=="enforce")continue;let value=input.value;
   if(key==="cloudApiKey"&&!value&&saved==="__KEEP_SECRET__")value=saved;
   else if(type==="boolean")value=value==="true";
   else if(type==="number"){if(!value.trim()||!Number.isSafeInteger(Number(value)))throw Error((labels[key]||key)+"需要整数");value=Number(value);}
   else if(type==="array"||type==="object"){value=JSON.parse(value);if(type==="array"?!Array.isArray(value):value===null||Array.isArray(value)||typeof value!=="object")throw Error((labels[key]||key)+"格式不正确");}
   result.settings[key]=value;
  }
  document.getElementById("policy-json").value=JSON.stringify(result);
 }catch(error){e.preventDefault();document.getElementById("policy-error").textContent=error.message;}
});
