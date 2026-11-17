import http from 'node:http';
import { timingSafeEqual } from 'node:crypto';
import { WebSocket, WebSocketServer } from 'ws';
import { Budget, validateRequest, generateTurn } from './meeting.mjs';

const host = process.env.HOST || '127.0.0.1';
const token = process.env.WINDUPS_GATEWAY_TOKEN || '';
const apiKey = process.env.OPENAI_API_KEY;
if (!['127.0.0.1','localhost','::1'].includes(host) && token.length < 24) throw new Error('Non-loopback binding requires a gateway token of at least 24 characters');
const budget = new Budget(); // Process-wide $1 conservative ceiling; restart deliberately to reset.
const completed = new Map();
let busy = false;
let speechBusy = false;
function authorized(req) {
  if (!token) return true;
  const supplied = Buffer.from(req.headers.authorization || '');
  const expected = Buffer.from(`Bearer ${token}`);
  return supplied.length === expected.length && timingSafeEqual(supplied, expected);
}
function reply(res, code, obj) { res.writeHead(code, {'Content-Type':'application/json'}); res.end(JSON.stringify(obj)); }
const server = http.createServer(async (req,res)=>{
  if (!authorized(req)) return reply(res,401,{error:'Unauthorized'});
  if (req.url === '/health' && req.method === 'GET') return reply(res,200,{ready:!!apiKey, budgetReservedUsd:budget.spent});
  if (req.url !== '/v1/turns' || req.method !== 'POST') return reply(res,404,{error:'Not found'});
  try {
    let body = '';
    for await (const chunk of req) { body += chunk; if (body.length > 24000) { reply(res,413,{error:'Request too large'}); return; } }
    const input = validateRequest(JSON.parse(body));
    const key = input.sessionId + ':' + input.requestId;
    if (completed.has(key)) return reply(res,200,completed.get(key));
    if (!apiKey) return reply(res,503,{error:'OPENAI_API_KEY is not configured. No AI request was made.'});
    if (busy) return reply(res,429,{error:'A turn is already in progress'});
    if (!budget.reserve(0.04)) return reply(res,429,{error:'Development budget reached'});
    busy = true;
    try {
      const result = await generateTurn(input,apiKey,process.env.OPENAI_MODEL || 'gpt-5.4-mini',AbortSignal.timeout(22000));
      completed.set(key,result);
      if (completed.size > 100) completed.delete(completed.keys().next().value);
      reply(res,200,result);
    } catch { reply(res,502,{error:'AI request failed or was incomplete. Retry manually.'}); }
    finally { busy = false; }
  } catch { if (!res.writableEnded) reply(res,400,{error:'Invalid meeting request'}); }
});
const wss = new WebSocketServer({noServer:true,maxPayload:48000});
server.on('upgrade',(req,socket,head)=>{
  if (req.url !== '/v1/speech' || !authorized(req)) { socket.end('HTTP/1.1 401 Unauthorized\r\n\r\n'); return; }
  wss.handleUpgrade(req,socket,head,ws=>wss.emit('connection',ws));
});
wss.on('connection',client=>{
  const send = obj => { if(client.readyState === WebSocket.OPEN) client.send(JSON.stringify(obj)); };
  if (!apiKey || speechBusy || !budget.reserve(0.04)) {
    send({type:'error',error:!apiKey?'OPENAI_API_KEY is not configured.':'Speech busy or development budget reached.'}); client.close(); return;
  }
  speechBusy = true;
  let bytes = 0, ready = false, committed = false;
  const upstream = new WebSocket('wss://api.openai.com/v1/realtime?intent=transcription',{headers:{Authorization:`Bearer ${apiKey}`}});
  const timer = setTimeout(()=>{send({type:'error',error:'Speech session timed out.'});client.close();},90000);
  upstream.on('open',()=>upstream.send(JSON.stringify({type:'session.update',session:{type:'transcription',audio:{input:{format:{type:'audio/pcm',rate:24000},transcription:{model:'gpt-live-transcribe'},turn_detection:null}}}})));
  upstream.on('message',data=>{
    try {
      const e=JSON.parse(data);
      if (e.type === 'session.updated') {ready=true;send({type:'ready'});}
      else if(e.type === 'conversation.item.input_audio_transcription.delta') send({type:'delta',delta:e.delta});
      else if(e.type === 'conversation.item.input_audio_transcription.completed') {send({type:'final',transcript:e.transcript});client.close();}
      else if(e.type === 'error' || e.type === 'conversation.item.input_audio_transcription.failed') {send({type:'error',error:'Transcription failed.'});client.close();}
    } catch {send({type:'error',error:'Invalid speech response.'});client.close();}
  });
  client.on('message',(data,binary)=>{
    if(!ready || committed) return;
    if(binary) {
      bytes += data.length;
      if(bytes > 24000*2*60 || data.length % 2 !== 0) {send({type:'error',error:'Audio limit exceeded.'});client.close();return;}
      upstream.send(JSON.stringify({type:'input_audio_buffer.append',audio:data.toString('base64')}));
    } else if(data.toString() === 'commit') {
      committed=true;
      if(bytes < 4800) {send({type:'error',error:'Recording was too short.'});client.close();return;}
      upstream.send(JSON.stringify({type:'input_audio_buffer.commit'}));
    }
  });
  upstream.on('error',()=>{send({type:'error',error:'Speech service connection failed.'});client.close();});
  upstream.on('close',()=>{if(client.readyState===WebSocket.OPEN) {send({type:'error',error:'Speech service disconnected.'});client.close();}});
  client.on('error',()=>client.close());
  client.on('close',()=>{clearTimeout(timer);speechBusy=false;upstream.close();});
});
server.listen(process.env.PORT === undefined ? 8787 : Number(process.env.PORT),host,()=>console.log(`Windups development gateway on ${host}:${server.address().port}; OpenAI ${apiKey?'configured':'not configured'}`));
