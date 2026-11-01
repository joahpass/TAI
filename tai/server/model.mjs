export function endpoint(config) {
  return config.baseUrl.replace(/\/+$/, '') + (config.provider === 'anthropic' ? (config.baseUrl.endsWith('/v1') ? '/messages' : '/v1/messages') : '/chat/completions');
}
export async function completion(config, messages, tools = [], signal) {
  if (!config.model) throw new Error('请先在设置中填写模型名称');
  const anthropic = config.provider === 'anthropic';
  let body;
  if (anthropic) {
    const converted = [];
    for (const m of messages.filter(m => m.role !== 'system')) {
      let role = m.role, content;
      if (role === 'tool') { role = 'user'; content = [{ type:'tool_result', tool_use_id:m.tool_call_id, content:m.content }]; }
      else { content = m.content ? [{ type:'text', text:m.content }] : []; for (const t of m.tool_calls ?? []) content.push({type:'tool_use',id:t.id,name:t.function.name,input:JSON.parse(t.function.arguments)}); }
      if (!content.length) continue;
      if (converted.at(-1)?.role === role) converted.at(-1).content.push(...content); else converted.push({role,content});
    }
    body = { model:config.model, system:messages.filter(m => m.role === 'system').map(m=>m.content).join('\n'), messages:converted, max_tokens:config.maxTokens, temperature:Math.min(config.temperature,1), ...(tools.length ? {tools:tools.map(t=>({name:t.name,description:t.description,input_schema:t.inputSchema}))} : {}) };
  } else body = {model:config.model,messages,max_tokens:config.maxTokens,temperature:config.temperature,...(tools.length ? {tools:tools.map(t=>({type:'function',function:{name:t.name,description:t.description,parameters:t.inputSchema}}))} : {})};
  const response = await fetch(endpoint(config), {
    method:'POST',headers:{'Content-Type':'application/json',...(anthropic ? {'x-api-key':config.apiKey,'anthropic-version':'2023-06-01'} : config.apiKey ? {Authorization:`Bearer ${config.apiKey}`} : {})},
    body:JSON.stringify(body),signal:AbortSignal.any([AbortSignal.timeout(180000),...(signal ? [signal] : [])])
  });
  if (!response.ok) {
    let detail = await response.text(); if (config.apiKey) detail = detail.split(config.apiKey).join('[redacted]');
    throw new Error(`模型请求失败 (${response.status})：${detail.slice(0,1000)}`);
  }
  const data = await response.json();
  if (anthropic) return {role:'assistant',content:(data.content??[]).filter(c=>c.type==='text').map(c=>c.text).join('\n'),tool_calls:(data.content??[]).filter(c=>c.type==='tool_use').map(c=>({id:c.id,type:'function',function:{name:c.name,arguments:JSON.stringify(c.input)}}))};
  const message = data.choices?.[0]?.message;
  if (!message) throw new Error('模型未返回有效消息，请检查协议和模型名称');
  return {role:'assistant',content:message.content ?? '',...(message.tool_calls ? {tool_calls:message.tool_calls} : {})};
}
