import test from 'node:test';
import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { once } from 'node:events';
import { WebSocket } from 'ws';

test('without a key, HTTP and speech fail explicitly; authentication is enforced',async()=>{
  const token='local-test-token-at-least-24-characters';
  const child=spawn(process.execPath,['src/server.mjs'],{cwd:new URL('..',import.meta.url),env:{...process.env,HOST:'127.0.0.1',PORT:'0',OPENAI_API_KEY:'',WINDUPS_GATEWAY_TOKEN:token},stdio:['ignore','pipe','pipe']});
  try {
    const [data]=await once(child.stdout,'data',{signal:AbortSignal.timeout(5000)});
    const port=data.toString().match(/127\.0\.0\.1:(\d+)/)[1];
    const url=`http://127.0.0.1:${port}`;
    assert.equal((await fetch(url+'/health')).status,401);
    const headers={Authorization:`Bearer ${token}`,'Content-Type':'application/json'};
    assert.equal((await (await fetch(url+'/health',{headers})).json()).ready,false);
    const r=await fetch(url+'/v1/turns',{method:'POST',headers,body:JSON.stringify({requestId:'r',sessionId:'s',trigger:'user',input:'an app',clarificationCount:0,history:[]})});
    assert.equal(r.status,503);assert.match((await r.json()).error,/not configured/);
    const ws=new WebSocket(`ws://127.0.0.1:${port}/v1/speech`,{headers});
    const [message]=await once(ws,'message',{signal:AbortSignal.timeout(5000)});
    assert.equal(JSON.parse(message).type,'error'); ws.close();
  } finally {child.kill();await once(child,'exit');}
});
