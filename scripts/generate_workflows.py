"""Generate portable, credential-free n8n workflow definitions."""
import json
from pathlib import Path
import uuid

ROOT=Path(__file__).resolve().parents[1]
def node(name, kind, parameters, x, version=1, **extra):
    return dict(id=str(uuid.uuid5(uuid.NAMESPACE_URL,'career-inbox/'+name)),name=name,type='n8n-nodes-base.'+kind,typeVersion=version,position=[x,0],parameters=parameters,**extra)
def field(label, kind='text', required=True, options=None):
    result=dict(fieldLabel=label,fieldType=kind,requiredField=required)
    if options: result['fieldOptions']={'values':[{'option':v} for v in options]}
    return result
def http(name,path,body):
    return node(name,'httpRequest',dict(method='POST',url='http://127.0.0.1:5680/'+path,sendBody=True,specifyBody='json',jsonBody=body,options={'timeout':15000}),300,4.2,onError='continueRegularOutput',retryOnFail=True,maxTries=3,waitBetweenTries=1000)
def completion():
    return node('Show result','form',dict(operation='completion',respondWith='text',completionTitle='Career Inbox result',completionMessage='={{ $json.message || "Could not complete this request. Check the execution error in n8n; no success is assumed." }}'),600,2.3)
def save(key,name,nodes):
    connections={a['name']:{'main':[[{'node':b['name'],'type':'main','index':0}]]} for a,b in zip(nodes,nodes[1:])}
    result=dict(id=key,name=name,nodes=nodes,connections=connections,settings={'executionOrder':'v1','timezone':'Asia/Manila','saveDataErrorExecution':'all','saveDataSuccessExecution':'none'},active=False,pinData={},tags=[])
    (ROOT/'workflows'/f'{key}.json').write_text(json.dumps(result,indent=2)+'\n',encoding='utf-8')

capture=node('Capture job','formTrigger',dict(path='career-inbox-capture',formTitle='Career Inbox — save a job',formDescription='Paste a job you found. Scoring is rule-based; verify vacancy status yourself. HourlyUsd means USD per hour, not monthly salary.',formFields={'values':[
    field('Url'),field('Title'),field('Company'),field('Description','textarea'),field('HourlyUsd','number',False),
    field('Eligibility','dropdown',True,['unknown','yes','no']),field('VacancyStatus','dropdown',True,['unverified','open','closed'])]},responseMode='lastNode',options={}),0,2.2,webhookId='career-inbox-capture')
body='={{ JSON.stringify({url:$json.Url,title:$json.Title,company:$json.Company,description:$json.Description,hourlyUsd:($json.HourlyUsd === "" || $json.HourlyUsd == null) ? null : Number($json.HourlyUsd),eligibility:$json.Eligibility,vacancyStatus:$json.VacancyStatus}) }}'
save('CareerInboxCapture','Career Inbox | Capture and rank',[capture,http('Validate deduplicate and rank','jobs',body),completion()])
status=node('Update application status','formTrigger',dict(path='career-inbox-status',formTitle='Career Inbox — update status',formDescription='Record what you did. This does not send an application or contact anyone.',formFields={'values':[field('Id','number'),field('Status','dropdown',True,['new','shortlisted','applied','interview','rejected','withdrawn','closed','excluded']),field('Note','textarea',False)]},responseMode='lastNode',options={}),0,2.2,webhookId='career-inbox-status')
update=http('Record status and audit event','status','={{ JSON.stringify({id:Number($json.Id),status:$json.Status,note:$json.Note || ""}) }}'); update['retryOnFail']=False
save('CareerInboxStatus','Career Inbox | Application status',[status,update,completion()])
schedule=node('Every morning at 8 Manila','scheduleTrigger',{'rule':{'interval':[{'field':'cronExpression','expression':'0 8 * * *'}]}},0,1.2)
digest=http('Generate private daily shortlist','digest','{}')
digest.pop('onError')
save('CareerInboxDigest','Career Inbox | Daily shortlist',[schedule,digest])
digest_file=ROOT/'workflows/CareerInboxDigest.json'
workflow=json.loads(digest_file.read_text(encoding='utf-8'))
manual=node('Run on demand','executeWorkflowTrigger',{'inputSource':'passthrough'},0,1.1)
manual['position']=[0,220]
workflow['nodes'].append(manual)
workflow['connections'][manual['name']]={'main':[[{'node':digest['name'],'type':'main','index':0}]]}
digest_file.write_text(json.dumps(workflow,indent=2)+'\n',encoding='utf-8')
print('Generated three n8n workflows.')
