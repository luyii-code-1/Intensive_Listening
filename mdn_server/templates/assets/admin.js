"use strict";
let statusBusy=false;
async function refreshOnline(force=false){
 if(statusBusy||(!force&&document.hidden))return;
 statusBusy=true;const deadline=new AbortController(),timer=setTimeout(()=>deadline.abort(),8000);
 try{
  const updates=document.querySelector('[data-panel="updates"]');
  const includeUpdates=updates&&!updates.hidden&&(force||document.getElementById("update-auto-refresh")?.checked);
  const params=new URLSearchParams({scope:new URLSearchParams(location.search).get("scope")||"all"});
  if(includeUpdates)params.set("updates","true");
  const response=await fetch("/admin/status?"+params,{cache:"no-store",signal:deadline.signal});
  if(response.redirected){location.assign(response.url);return;}
  if(!response.ok)throw Error("状态同步失败（HTTP "+response.status+"）");
  const {devices,updates_html,device_rows_html}=await response.json();
  const deviceBody=document.getElementById("device-rows");
  const known=Array.from(deviceBody.querySelectorAll("[data-id]")).map(row=>row.dataset.id).sort().join(",");
  const incoming=devices.map(device=>device.id).sort().join(",");
  if(known!==incoming&&!deviceBody.contains(document.activeElement)&&typeof device_rows_html==="string"){
   deviceBody.innerHTML=device_rows_html;
   document.querySelectorAll(".scope-picker").forEach(picker=>{const selected=picker.value;while(picker.options.length>1)picker.remove(1);for(const device of devices){const option=document.createElement("option");option.value=device.id;option.textContent=device.name+" · "+device.id.slice(-12);picker.append(option);}picker.value=Array.from(picker.options).some(option=>option.value===selected)?selected:"all";});
  }
  const count=devices.filter(device=>device.online).length;
  for(const device of devices){
   const row=document.querySelector(`[data-id="${device.id}"]`);if(!row)continue;
   const state=!device.enabled?"disabled":device.online?"online":"offline";
   row.dataset.status=state;const badge=row.querySelector(".badge");badge.classList.toggle("online",state==="online");badge.textContent=state==="disabled"?"已禁用":state==="online"?"在线":"离线";
   const policy=row.querySelector(".policy-state");if(policy){policy.textContent=device.policy_state;policy.title=device.policy_detail;policy.className="policy-state "+(device.policy_current?"confirmed":"pending");}
   const connection=row.querySelector(".connection-state");if(connection)connection.textContent=device.connection;
   const role=row.querySelector(".role-state");if(role)role.textContent=device.role_label;
   const warning=row.querySelector(".role-warning");if(warning){warning.textContent=device.role_warning;warning.hidden=!device.role_warning;}
   const asr=row.querySelector(".asr-state");if(asr)asr.textContent=device.asr_label;
   const asrWarning=row.querySelector(".asr-warning");if(asrWarning){asrWarning.textContent=device.asr_warning;asrWarning.hidden=!device.asr_warning;}
   if(device.cloud){
    const cloud=device.cloud,put=(selector,value)=>{const element=row.querySelector(selector);if(element){element.textContent=value;element.hidden=!value;}};
    put(".cloud-status",cloud.statusText);put(".cloud-account",cloud.accountText);put(".cloud-warning",cloud.warning);put(".cloud-command",cloud.commandText);
    put(".cloud-reported",cloud.reportedText?"最近上报："+cloud.reportedText+(cloud.stale?" · 终端离线":""):"");
    const form=row.querySelector('form[action="/admin/cloud/logout"]');if(form&&!form.dataset.busy)form.querySelector("button").disabled=!cloud.canLogout;
   }
   const version=row.querySelector(".device-version");if(version)version.textContent=device.version||"版本未知";
   const seen=row.querySelector("td:nth-child(3)>small");if(seen)seen.textContent=device.seen_text;
   const name=row.querySelector("td:first-child>a"),original=row.querySelector("td:first-child>small");if(name)name.textContent=device.name;if(original)original.textContent=device.original_name;
   row.dataset.device=device.name+" "+device.original_name+" "+device.id;
  }
  document.getElementById("online-count").textContent=count;
  document.getElementById("offline-count").textContent=devices.length-count;
  document.getElementById("registered-count").textContent=devices.length;
  filterDevices();
  if(includeUpdates&&typeof updates_html==="string"){
   const host=document.getElementById("update-live"),selection=getSelection();
   const selecting=selection?.toString()&&host.contains(selection.anchorNode);
   const editing=host.contains(document.activeElement)&&document.activeElement.matches("input,textarea,select");
   if(!selecting&&!editing&&host.dataset.lastHtml!==updates_html){
    const open=new Set(Array.from(host.querySelectorAll("details[open][data-update-details]")).map(item=>item.dataset.updateDetails));
    host.innerHTML=updates_html;host.dataset.lastHtml=updates_html;
    host.querySelectorAll("details[data-update-details]").forEach(item=>item.open=open.has(item.dataset.updateDetails));
   }
   document.getElementById("update-sync-status").textContent="已同步 "+new Date().toLocaleTimeString();
  }
 }catch{const status=document.getElementById("update-sync-status");if(status)status.textContent="状态同步失败，将自动重试";}
 finally{clearTimeout(timer);statusBusy=false;}
}
setInterval(()=>refreshOnline(),3000);
document.addEventListener("visibilitychange",()=>{if(!document.hidden)refreshOnline();});
document.getElementById("update-refresh")?.addEventListener("click",()=>refreshOnline(true));
document.getElementById("update-auto-refresh")?.addEventListener("change",event=>{if(event.target.checked)refreshOnline(true);else document.getElementById("update-sync-status").textContent="自动刷新已暂停";});
function deploymentPrompt(form){const count=Number(document.getElementById("update-feedback")?.dataset.replacementCount||0);return `向当前范围下发版本 ${form.elements.version.value}？`+(count?`\n${count} 台设备的旧任务将被新任务替换，历史反馈会保留。`:"\n每台设备同时只执行一个更新任务。")+"\n已启动的安装会继续完成。";}
document.getElementById("upload-form").addEventListener("submit",event=>{
 event.preventDefault();const form=event.currentTarget,file=form.elements.package.files[0],status=document.getElementById("upload-status"),button=form.querySelector("button"),progress=document.getElementById("upload-progress");
 if(!file||file.size>2147483648){status.textContent="请选择不超过 2 GiB 的 EXE 安装包。";return;}
 if(!confirm(deploymentPrompt(form)))return;
 const params=new URLSearchParams({scope:form.elements.scope.value,mode:form.elements.mode.value,version:form.elements.version.value,filename:file.name});
 const request=new XMLHttpRequest();request.open("POST","/admin/update/upload?"+params);
 request.setRequestHeader("X-CSRF-Token",form.elements.form_token.value);request.setRequestHeader("Content-Type","application/octet-stream");
 button.disabled=true;progress.hidden=false;progress.value=0;status.textContent="正在上传安装包…";
 request.upload.onprogress=event=>{if(event.lengthComputable){progress.value=event.loaded/event.total;status.textContent=`正在上传：${Math.round(progress.value*100)}%`;}};
 request.upload.onload=()=>{status.textContent="安装包已上传，正在校验、签名并下发…";};
 request.onload=()=>{
  if(new URL(request.responseURL).pathname==="/"){location.assign(request.responseURL);return;}
  try{const result=JSON.parse(request.responseText);if(request.status>=200&&request.status<300){location.assign(result.redirect);return;}status.textContent=result.detail||"上传失败，请重试。";}catch{status.textContent="上传失败，请检查服务器连接。";}
  button.disabled=false;
 };
 request.onerror=()=>{status.textContent="连接中断，上传失败，请重试。";button.disabled=false;};
 request.send(file);
});
const labels={asrProvider:"识别引擎",cloudBaseUrl:"ASR 服务地址",cloudEndpoint:"ASR 路径",cloudModel:"ASR 模型",cloudApiKey:"ASR API Key",cloudTimeoutSeconds:"ASR 超时（秒）",cloudConcurrency:"ASR 并发数",cloudLanguage:"识别语言",translateChineseToEnglish:"中文翻译为英文",fileAssociationEnabled:"ILP 文件关联",mcpEnabled:"智能体 MCP 服务",localModelsDirectory:"本地模型目录",selectedLocalModel:"本地模型",detectedLocalModels:"已发现模型",themeMode:"应用主题",applicationFontFamily:"应用字体",applicationFontWeight:"字体字重",skipOpeningPrompts:"跳过打开提示",createProjectOnAudioImport:"导入音频时建立项目",transcriptFontSize:"字幕字号",playerKeyBindings:"播放器快捷键",debugLogging:"调试日志"};
const featureLabels={playback:"播放与跳转",importAudio:"导入音频",importIlp:"导入精听包",authoring:"课程制作",asr:"音频转写",mcp:"智能体接管",export:"课程导出",importProject:"导入工程",dataMaintenance:"数据维护",apiConfiguration:"API 配置操作",updates:"组织更新"};
const payload=JSON.parse(document.getElementById("initial-policy").textContent),schema=JSON.parse(document.getElementById("settings-schema").textContent),features=JSON.parse(document.getElementById("features-schema").textContent),query=new URLSearchParams(location.search);
labels.transcriptBlurIntensity="高速模糊强度";
function tab(name){if(!["devices","policy","lessons","updates","telemetry","connection"].includes(name))name="devices";document.querySelectorAll("[data-panel]").forEach(p=>p.hidden=p.dataset.panel!==name);document.querySelectorAll("[data-tab]").forEach(b=>{b.classList.toggle("active",b.dataset.tab===name);b.setAttribute("aria-current",b.dataset.tab===name?"page":"false");});document.getElementById("page-title").textContent=document.querySelector(`[data-tab="${name}"]`).textContent.trim();query.set("view",name);history.replaceState(null,"","?"+query);if(name==="updates")refreshOnline();}
document.querySelectorAll("[data-tab]").forEach(b=>b.addEventListener("click",()=>tab(b.dataset.tab)));tab(query.get("view")||(query.get("scope")&&query.get("scope")!=="all"?"policy":"devices"));
document.querySelectorAll(".scope-picker").forEach(s=>s.addEventListener("change",()=>{query.set("scope",s.value);location.search=query.toString();}));
function filterDevices(){const search=document.getElementById("device-search").value.toLowerCase(),status=document.getElementById("device-status").value;document.querySelectorAll("[data-device]").forEach(r=>r.hidden=!r.dataset.device.toLowerCase().includes(search)||(status!=="all"&&r.dataset.status!==status));}
document.getElementById("device-search").addEventListener("input",filterDevices);document.getElementById("device-status").addEventListener("change",filterDevices);
document.addEventListener("submit", async event=>{
 const form=event.target;if(event.defaultPrevented)return;
 if(form.matches("form[data-update-publish]")){if(!confirm(deploymentPrompt(form))){event.preventDefault();return;}}
 else if(form.matches("form[data-confirm]")&&!confirm(form.dataset.confirm)){event.preventDefault();return;}
 const action=new URL(form.action).pathname;
 if(!["/admin/policy","/admin/device/alias","/admin/updates/retry","/admin/update/cancel","/admin/updates/clear","/admin/cloud/logout"].includes(action))return;
 event.preventDefault();if(form.dataset.busy)return;form.dataset.busy="true";
 const feedback=document.getElementById("action-feedback"),buttons=Array.from(form.querySelectorAll("button"));
 const body=new FormData(form);buttons.forEach(button=>button.disabled=true);
 feedback.hidden=false;feedback.className="action-feedback";feedback.textContent="正在提交…";
 const deadline=new AbortController(),timer=setTimeout(()=>deadline.abort(),20000);
 try{
  const response=await fetch(form.action,{method:"POST",body,signal:deadline.signal});
  if(response.redirected&&new URL(response.url).pathname!=="/admin"){location.assign(response.url);return;}
  if(!response.ok){let message="操作失败（HTTP "+response.status+"）";try{const data=await response.json();if(typeof data.detail==="string")message=data.detail;else if(data.detail)message=JSON.stringify(data.detail);}catch{}throw Error(message);}
  if(action==="/admin/cloud/logout"){const result=await response.json();form.elements.command_id.value=result.nextCommandId;}
  feedback.className="action-feedback confirmed";
  feedback.textContent=action==="/admin/cloud/logout"?"退出命令已保存，等待终端执行。可在设备列表查看结果。":action==="/admin/policy"?"策略已保存并通知客户端。设备列表可查看策略接收回执。":action==="/admin/updates/retry"?"新任务已下发，等待客户端接收。":"操作已完成。";
  if(action!=="/admin/cloud/logout")await refreshOnline(true);
 }catch(error){feedback.className="action-feedback failed";feedback.textContent=error.name==="AbortError"?"等待响应超时，操作可能已保存；请先刷新状态再决定是否重试。":error.message;}
 finally{clearTimeout(timer);delete form.dataset.busy;buttons.forEach(button=>button.disabled=action==="/admin/cloud/logout");if(action==="/admin/cloud/logout")refreshOnline(true);}
});
function select(options){const s=document.createElement("select");for(const[value,label]of options){const o=document.createElement("option");o.value=value;o.textContent=label;s.append(o);}return s;}
document.querySelectorAll("[data-option]").forEach(s=>s.value=Object.hasOwn(payload,s.dataset.option)?String(payload[s.dataset.option]):"inherit");
document.querySelectorAll("[data-cloud-option]").forEach(input=>{const key=input.dataset.cloudOption;input.value=Object.hasOwn(payload.cloudService||{},key)?String(payload.cloudService[key]):"inherit";});
const clientRole=document.getElementById("client-role");clientRole.value=payload.clientRole||"inherit";
const asrMode=document.getElementById("asr-mode");asrMode.value=payload.asrMode||"inherit";
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
  const result={settings:{},lockedSettings:[]};if(clientRole.value!=="inherit")result.clientRole=clientRole.value;if(asrMode.value!=="inherit")result.asrMode=asrMode.value;document.querySelectorAll("[data-option]").forEach(s=>{if(s.value!=="inherit")result[s.dataset.option]=s.value==="true";});
  const cloud={};document.querySelectorAll("[data-cloud-option]").forEach(input=>{if(input.value!=="inherit")cloud[input.dataset.cloudOption]=input.dataset.cloudOption==="mode"?input.value:input.value==="true";});if(Object.keys(cloud).length)result.cloudService=cloud;
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
document.getElementById("lesson-audience").addEventListener("change",event=>{
 const selected=event.target.value==="selected";document.getElementById("lesson-target-label").hidden=!selected;document.getElementById("lesson-targets").disabled=!selected;
});
document.getElementById("lesson-upload-form").addEventListener("submit",event=>{
 event.preventDefault();const form=event.currentTarget,file=form.elements.package.files[0],status=document.getElementById("lesson-upload-status"),button=form.querySelector("button"),progress=document.getElementById("lesson-upload-progress");
 const targets=form.elements.audience.value==="all"?["all"]:Array.from(form.elements.targets.selectedOptions).map(option=>option.value);
 if(!file||!file.name.toLowerCase().endsWith(".ilp")||file.size>2147483648||!targets.length){status.textContent="请选择不超过 2 GiB 的精听包和接收设备。";return;}
 if(!confirm("向所选范围下发此精听包？客户端将自动下载并导入。"))return;
 const request=new XMLHttpRequest();request.open("POST","/admin/lessons/upload?"+new URLSearchParams({scopes:targets.join(","),filename:file.name}));
 request.setRequestHeader("X-CSRF-Token",form.elements.form_token.value);request.setRequestHeader("Content-Type","application/octet-stream");
 button.disabled=true;progress.hidden=false;progress.value=0;status.textContent="正在上传精听包…";
 request.upload.onprogress=event=>{if(event.lengthComputable){progress.value=event.loaded/event.total;status.textContent=`正在上传：${Math.round(progress.value*100)}%`;}};
 request.onload=()=>{if(new URL(request.responseURL).pathname==="/"){location.assign(request.responseURL);return;}try{const result=JSON.parse(request.responseText);if(request.status>=200&&request.status<300){location.assign(result.redirect);return;}status.textContent=result.detail||"上传失败，请重试。";}catch{status.textContent="上传失败，请检查服务器连接。";}button.disabled=false;};
 request.onerror=()=>{status.textContent="连接中断，请重试。";button.disabled=false;};request.send(file);
});
