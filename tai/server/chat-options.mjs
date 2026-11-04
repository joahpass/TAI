export const reasoningEfforts = ['auto','off','low','medium','high'];
export function validateModel(value, {allowEmpty=false}={}) {
  if(typeof value!=='string'||(!allowEmpty&&!value.trim())||value.length>200||/[\x00-\x1f\x7f-\x9f]/u.test(value)) throw new Error('model 必须为 1–200 字符且不含控制字符的文本');
  return value.trim();
}
export function validateEffort(value) {
  if(!reasoningEfforts.includes(value)) throw new Error('reasoningEffort 必须为 auto/off/low/medium/high');
  return value;
}
// Recognized direct Anthropic model families; unknown IDs use the documented manual budget path.
export function supportsAdaptive(model) {
  // Documented adaptive families (not a discovery catalog). Do not infer support for future versions.
  return /^claude-(?:(?:opus-4[.-](?:6|7|8)|sonnet-4[.-]6)|(?:(?:haiku|sonnet|opus)-5[.-]5)|(?:(?:fable|mythos)-5[.-]1)|(?:(?:opus|sonnet|mythos|fable)-5))(?:-(?:\d{8}|latest))?$/i.test(model);
}
export function reasoningParameters(config) {
  const effort=validateEffort(config.reasoningEffort??'auto');
  if(effort==='auto')return {};
  if(config.provider!=='anthropic')return {reasoning_effort:effort==='off'?'none':effort};
  if(effort==='off')return {thinking:{type:'disabled'}};
  if(supportsAdaptive(config.model))return {thinking:{type:'adaptive'},output_config:{effort}};
  const budget=Math.min({low:1024,medium:4096,high:8192}[effort],config.maxTokens-1);
  if(!Number.isInteger(budget)||budget<1024)throw new Error('Anthropic thinking 要求 maxTokens 至少 1025（budget_tokens 至少 1024 且小于 max_tokens）');
  return {thinking:{type:'enabled',budget_tokens:budget}};
}
export function resolveChatSelection(config,input={}) {
  const model=input.model===undefined?(config.model??''):validateModel(input.model);
  if(model)validateModel(model);
  const reasoningEffort=validateEffort(input.reasoningEffort===undefined?(config.reasoningEffort??'auto'):input.reasoningEffort);
  const snapshot=structuredClone({...config,model,reasoningEffort});
  reasoningParameters(snapshot);
  return Object.freeze(snapshot);
}
export function validateChatInput(input,config) {
  if(!input||typeof input!=='object'||Array.isArray(input))throw new Error('请求必须为对象');
  const allowed=new Set(['conversationId','message','mode','model','reasoningEffort']);
  if(Object.keys(input).some(k=>!allowed.has(k)))throw new Error('未知的聊天参数');
  if(typeof input.conversationId!=='string'||!input.conversationId)throw new Error('必须提供 conversationId');
  if(typeof input.message!=='string'||!input.message.trim()||input.message.length>40000)throw new Error('请输入 1–40000 字符的消息');
  if(!['read','artifact','write'].includes(input.mode))throw new Error('无效的工作模式');
  return resolveChatSelection(config,input);
}
