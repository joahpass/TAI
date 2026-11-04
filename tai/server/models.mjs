import { createHash } from 'node:crypto';
import { endpoint, modelHeaders, redactError } from './model.mjs';
import { validateModel } from './chat-options.mjs';
export function createModelDiscovery({fetchImpl=(...args)=>fetch(...args),now=Date.now,timeoutMs=8000}={}) {
  const cache=new Map(),pending=new Map();
  return async function discover(configuration,{refresh=false}={}) {
    const config=structuredClone(configuration);
    const key=createHash('sha256').update(JSON.stringify(config)).digest('hex');
    if(pending.has(key))return structuredClone(await pending.get(key));
    if(!refresh&&cache.has(key)&&now()-cache.get(key).at<300000)return structuredClone(cache.get(key).value);
    const work=(async()=>{
      const result={models:config.model?[{id:config.model}]:[],provider:config.provider,defaultModel:config.model||'',reasoningEffort:config.reasoningEffort??'auto',source:'configured'};
      const signal=AbortSignal.timeout(timeoutMs);
      try {
        const models=[],seen=new Set();let cursor;
        do {
          const url=new URL(endpoint(config,'models'));
          if(config.provider==='anthropic'){url.searchParams.set('limit','1000');if(cursor)url.searchParams.set('after_id',cursor);}
          const response=await fetchImpl(url.toString(),{headers:modelHeaders(config),signal,redirect:'error'});
          if(!response.ok)throw new Error(`模型列表请求失败 (${response.status})：${await response.text()}`);
          const data=await response.json();
          if(!Array.isArray(data.data))throw new Error('模型列表响应缺少 data 数组');
          for(const item of data.data) {
            try{validateModel(item?.id);}catch{continue;}
            if(seen.has(item.id))continue;
            if(config.apiKey&&(item.id.includes(config.apiKey)||String(item.display_name??'').includes(config.apiKey)))continue;
            seen.add(item.id);models.push({id:item.id,...(typeof item.display_name==='string'?{label:item.display_name.slice(0,200)}:{})});
          }
          const next=config.provider==='anthropic'&&data.has_more?data.last_id:null;
          if(next&&(typeof next!=='string'||next===cursor||!data.data.length))throw new Error('模型列表分页游标无效');
          cursor=next;
        }while(cursor);
        result.models=models;result.source='remote';
      }catch(error){result.error=redactError(error.message,config);}
      cache.set(key,{at:now(),value:result});
      // Bound retained entries without mixing configurations or retaining API keys.
      if(cache.size>32)cache.delete(cache.keys().next().value);
      return result;
    })();
    pending.set(key,work);
    try{return structuredClone(await work);}finally{pending.delete(key);}
  };
}
