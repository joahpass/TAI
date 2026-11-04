import { reasoningParameters, resolveChatSelection } from './chat-options.mjs';
export function endpoint(config, resource='completion') {
  const base=config.baseUrl.replace(/\/+$/, '');
  return base+(config.provider==='anthropic' ? (base.endsWith('/v1')?'':'/v1')+(resource==='models'?'/models':'/messages') : resource==='models'?'/models':'/chat/completions');
}
export function modelHeaders(config) {
  return {'Content-Type':'application/json',...(config.provider==='anthropic'?{'x-api-key':config.apiKey,'anthropic-version':'2023-06-01'}:config.apiKey?{Authorization:`Bearer ${config.apiKey}`}:{})};
}
export function redactError(value,config) {
  let text=String(value);
  for(const secret of [config.apiKey,config.apiKey?encodeURIComponent(config.apiKey):''])if(secret)text=text.split(secret).join('[redacted]');
  return text.replace(/(Bearer\s+)[^\s"',;]+/gi,'$1[redacted]').replace(/((?:api[_-]?key|authorization|token)\s*[=:]\s*)[^\s,"'}]+/gi,'$1[redacted]').slice(0,1000);
}
export function requestBody(config,messages,tools=[]) {
  const anthropic=config.provider==='anthropic',reasoning=reasoningParameters(config);
  if(anthropic) {
    const converted=[];
    for(const m of messages.filter(m=>m.role!=='system')) {
      let role=m.role,content;
      if(role==='tool'){role='user';content=[{type:'tool_result',tool_use_id:m.tool_call_id,content:m.content}];}
      else if(m.anthropicContent)content=structuredClone(m.anthropicContent);
      else {content=m.content?[{type:'text',text:m.content}]:[];for(const t of m.tool_calls??[])content.push({type:'tool_use',id:t.id,name:t.function.name,input:JSON.parse(t.function.arguments)});}
      if(!content.length)continue;
      if(converted.at(-1)?.role===role)converted.at(-1).content.push(...content);else converted.push({role,content});
    }
    return {model:config.model,system:messages.filter(m=>m.role==='system').map(m=>m.content).join('\n'),messages:converted,max_tokens:config.maxTokens,...reasoning,...(!reasoning.thinking||reasoning.thinking.type==='disabled'?{temperature:Math.min(config.temperature,1)}:{}),...(tools.length?{tools:tools.map(t=>({name:t.name,description:t.description,input_schema:t.inputSchema}))}:{})};
  }
  return {model:config.model,messages:messages.map(({anthropicContent,...m})=>m),[/^(?:o[134](?:-|$)|gpt-5(?:[.-]|$))/i.test(config.model)?'max_completion_tokens':'max_tokens']:config.maxTokens,...reasoning,...((config.reasoningEffort??'auto')==='auto'&&!/^(?:o[134](?:-|$)|gpt-5(?:[.-]|$))/i.test(config.model)?{temperature:config.temperature}:{}),...(tools.length?{tools:tools.map(t=>({type:'function',function:{name:t.name,description:t.description,parameters:t.inputSchema}}))}:{})};
}
export async function completion(config,messages,tools=[],signal) {
  config=resolveChatSelection(config);
  if(!config.model)throw new Error('请先在设置中填写模型名称');
  const response=await fetch(endpoint(config),{method:'POST',headers:modelHeaders(config),body:JSON.stringify(requestBody(config,messages,tools)),signal:AbortSignal.any([AbortSignal.timeout(180000),...(signal?[signal]:[])])});
  if(!response.ok)throw new Error(`模型请求失败 (${response.status})：${redactError(await response.text(),config)}`);
  const data=await response.json();
  if(config.provider==='anthropic')return {role:'assistant',content:(data.content??[]).filter(c=>c.type==='text').map(c=>c.text).join('\n'),anthropicContent:structuredClone(data.content??[]),tool_calls:(data.content??[]).filter(c=>c.type==='tool_use').map(c=>({id:c.id,type:'function',function:{name:c.name,arguments:JSON.stringify(c.input)}}))};
  const message=data.choices?.[0]?.message;
  if(!message)throw new Error('模型未返回有效消息，请检查协议和模型名称');
  return {role:'assistant',content:message.content??'',...(message.tool_calls?{tool_calls:message.tool_calls}:{})};
}
