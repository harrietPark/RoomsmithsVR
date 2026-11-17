import test from 'node:test';
import assert from 'node:assert/strict';
import { validateRequest, normalizeResponse, Budget } from '../src/meeting.mjs';
const request = () => ({requestId:'r1',sessionId:'s1',trigger:'user',input:'I want to make an app',clarificationCount:0,history:[]});
const line = speaker=>({speaker,summary:'Who is it for?',body:'Who would you like to help?',replyTo:'',relation:'question',cue:'speak'});
test('one Decisions clarification receives a stable server id',()=>{
  const response=normalizeResponse({clarification:true,utterances:[line('Decisions')]},validateRequest(request()));
  assert.ok(response.utterances[0].id); assert.equal(response.utterances[0].speaker,'Decisions');
});
test('reject clarification loops and wrong facilitator',()=>{
  assert.throws(()=>normalizeResponse({clarification:true,utterances:[line('Benefit')]},request()));
  assert.throws(()=>normalizeResponse({clarification:true,utterances:[line('Decisions')]},{...request(),clarificationCount:2}));
});
test('reject duplicate actors, invented reply ids and excessive reactions',()=>{
  assert.throws(()=>normalizeResponse({clarification:false,utterances:[line('Benefit'),line('Benefit')]},request()));
  assert.throws(()=>normalizeResponse({clarification:false,utterances:[{...line('Caution'),replyTo:'not-real'}]},request()));
  assert.throws(()=>normalizeResponse({clarification:false,utterances:['Caution','Benefit','Creativity'].map(line)},{...request(),trigger:'reaction'}));
});
test('valid disagreement refers to a presented idea',()=>{
  const r={...request(),trigger:'reaction',history:[{id:'p1',speaker:'Benefit',body:'A value'}]};
  assert.equal(normalizeResponse({clarification:false,utterances:[{...line('Caution'),replyTo:'p1',relation:'challenge'}]},r).utterances[0].replyTo,'p1');
});
test('bounded input and conservative spending ceiling',()=>{
  assert.throws(()=>validateRequest({...request(),input:'x'.repeat(3001)}));
  const b=new Budget(); assert.equal(b.reserve(.6),true);assert.equal(b.reserve(.6),false);assert.equal(b.spent,.6);
});
