"""Independent synthetic Python/cryptography vectors. No private key is retained.
Run manually from any directory; production code never imports this generator.
"""
from pathlib import Path
import base64
import copy
import hashlib
import json
from cryptography.hazmat.primitives import hashes, serialization
from cryptography.hazmat.primitives.asymmetric import ec, utils


def canonical(value):
    return json.dumps(value, sort_keys=True, separators=(',', ':'), ensure_ascii=False).encode('utf-8')


def sha(value):
    return hashlib.sha256(value).hexdigest()


def b64(value):
    return base64.urlsafe_b64encode(value).rstrip(b'=').decode('ascii')


directory = Path(__file__).resolve().parent / 'v1'
library = '94a81164-e980-4ec1-a566-cec36475b839'
actor = '6c803c09-3b11-49cb-a3dc-1d42c3dc6f30'
workspace = dict(accountId='workspace-account', folderId='workspace-folder')
key = ec.generate_private_key(ec.SECP256R1())
public = key.public_key().public_bytes(serialization.Encoding.DER, serialization.PublicFormat.SubjectPublicKeyInfo)
connection = dict(format='mpa-library-connection', major=2, libraryId=library, currentFileId='current-control', workspace=workspace,
    controller=dict(actorId=actor, epoch='1', keyId=sha(public), publicKeySpki=base64.b64encode(public).decode('ascii')))


def code(value):
    return 'mpa-library-v2:' + b64(canonical(value))


def sign(payload, sequence, kind='library-current-control', capabilities=None, signing_key=key, **fields):
    document = dict(format='mpa', major=1, minor=0, libraryId=library, actorId=actor, epoch='1', sequence=str(sequence),
        kind=kind, requiredCapabilities=capabilities if capabilities is not None else ['library.current.v1'], payload=payload)
    document.update(fields)
    r, s = utils.decode_dss_signature(signing_key.sign(canonical(document), ec.ECDSA(hashes.SHA256())))
    return canonical(dict(format='mpa-signed-document', major=1, document=document,
        signature=dict(algorithm='ES256', keyId=sha(signing_key.public_key().public_bytes(serialization.Encoding.DER, serialization.PublicFormat.SubjectPublicKeyInfo)), value=b64(r.to_bytes(32, 'big') + s.to_bytes(32, 'big'))))).decode('utf-8')


def reference(name, body=b'synthetic immutable bytes'):
    return dict(accountId=workspace['accountId'], workspaceId=workspace['folderId'], fileId=name, bytes=str(len(body)), sha256=sha(body))


publisher_payload = json.loads(json.loads((directory/'catalog-publisher-authorization.json').read_bytes())['active'])['document']['payload']
source_payload = json.loads((directory/'catalog-source-configuration.json').read_bytes())['valid']['payload']
publisher = sign(publisher_payload, 9, 'library-publisher-authorization', ['library.publishers.v1'])
sources = sign(source_payload, 10, 'library-source-configuration', ['library.sources.v1'])
payload = dict(workspace=workspace, currentFileId='current-control', publishers=dict(object=reference('publishers',publisher.encode()),sequence='9'),
    sources=dict(object=reference('sources',sources.encode()),sequence='10'), catalog=reference('catalog'))
first = sign(payload,11)
invalid = []


def bad(name, mutate, **fields):
    value = copy.deepcopy(payload);mutate(value)
    invalid.append(dict(name=name,envelope=sign(value,11,**fields)))


bad('retarget-control',lambda p:p.update(currentFileId='copy-control'))
bad('foreign-account',lambda p:p['workspace'].update(accountId='other'))
bad('foreign-folder',lambda p:p['workspace'].update(folderId='other'))
bad('extra-field',lambda p:p.update(grantsOriginalWrite=True))
bad('missing-field',lambda p:p.pop('catalog'))
bad('wrong-kind',lambda p:None,kind='user-operation')
bad('missing-capability',lambda p:None,capabilities=[])
bad('foreign-library',lambda p:None,libraryId='00000000-0000-4000-8000-000000000001')
bad('foreign-controller',lambda p:None,actorId='00000000-0000-4000-8000-000000000001')
bad('foreign-epoch',lambda p:None,epoch='2')
bad('same-policy-sequence',lambda p:p['sources'].update(sequence='9'))
for role in ('publishers','sources'):
    for name,value in [('equal-control','11'),('future','12'),('leading-zero','09'),('overflow','9223372036854775808'),('numeric',9),('zero','0')]:
        bad(role+'-'+name,lambda p,r=role,v=value:p[r].update(sequence=v))
for name,field,value in [('root-alias','fileId','root'),('workspace-alias','fileId','workspace-folder'),('control-alias','fileId','current-control'),
                         ('cross-role','fileId','publishers'),('foreign-account','accountId','other'),('foreign-folder','workspaceId','other'),
                         ('zero-length','bytes','0'),('oversize','bytes','262145'),('noncanonical-length','bytes','01'),
                         ('uppercase-hash','sha256','A'*64),('truncated-hash','sha256','a'*63),('path-id','fileId','../metadata')]:
    bad(name,lambda p,f=field,v=value:p['catalog'].update({f:v}))
bad('policy-null',lambda p:p.update(publishers=None))
bad('policy-extra',lambda p:p['sources'].update(permission=True))
bad('reference-extra',lambda p:p['catalog'].update(permission=True))
bad('workspace-extra',lambda p:p['workspace'].update(endpoint='http://localhost'))

valid = [dict(name='first',envelope=first),dict(name='unchanged-roles-new-head',envelope=sign(payload,12))]
changed = copy.deepcopy(payload);changed['sources']=dict(object=reference('sources-new'),sequence='12')
valid.append(dict(name='changed-role-after-prior-head',envelope=sign(changed,13)))
changed_catalog = copy.deepcopy(payload);changed_catalog['catalog']=reference('catalog-new')
valid.append(dict(name='changed-catalog',envelope=sign(changed_catalog,14)))
valid.append(dict(name='maximum-sequence',envelope=sign(payload,9223372036854775807)))
transitions=[]
def transition(name, value, sequence):
    transitions.append(dict(name=name,envelope=sign(value,sequence)))
older=copy.deepcopy(payload);older['publishers']['sequence']='7';older['sources']['sequence']='8'
transition('rewind',older,9)
fork=copy.deepcopy(payload);fork['catalog']=reference('fork')
transition('same-sequence-fork',fork,11)
orphan=copy.deepcopy(payload);orphan['sources']['object']=reference('orphan-source')
transition('old-source-orphan',orphan,12)
orphan=copy.deepcopy(payload);orphan['publishers']['object']=reference('orphan-publisher')
transition('old-publisher-orphan',orphan,12)
revoked = sign(dict(workspace=workspace,publisher=None),12,'library-publisher-authorization',['library.publishers.v1'])
revocation=copy.deepcopy(payload);revocation['publishers']=dict(object=reference('revoked',revoked.encode()),sequence='12');revocation['catalog']=None
revocation_bad=copy.deepcopy(revocation);revocation_bad['catalog']=reference('stale-catalog')
wrong_sequence=copy.deepcopy(payload);wrong_sequence['publishers']['sequence']='8'
foreign=ec.generate_private_key(ec.SECP256R1())
forged_publisher=sign(publisher_payload,9,'library-publisher-authorization',['library.publishers.v1'],signing_key=foreign)
forged_sources=sign(source_payload,10,'library-source-configuration',['library.sources.v1'],signing_key=foreign)
forged_payload=copy.deepcopy(payload)
forged_payload['publishers']['object']=reference('forged-publisher',forged_publisher.encode())
forged_payload['sources']['object']=reference('forged-sources',forged_sources.encode())
invalid_codes=[]
for name,field,value in [('root','currentFileId','root'),('folder','currentFileId','workspace-folder'),('path','currentFileId','../metadata'),
                         ('empty','currentFileId',''),('null','currentFileId',None),('wrong-major','major',1),('extra','endpoint','http://localhost')]:
    value_copy=copy.deepcopy(connection);value_copy[field]=value
    invalid_codes.append(dict(name=name,code=code(value_copy)))
missing=copy.deepcopy(connection);missing.pop('currentFileId');invalid_codes.append(dict(name='missing-control',code=code(missing)))
retarget=copy.deepcopy(connection);retarget['currentFileId']='other-control'
retarget_payload=copy.deepcopy(payload);retarget_payload['currentFileId']='other-control'
legacy=copy.deepcopy(connection);legacy.pop('currentFileId');legacy['major']=1
result=dict(note='Independent synthetic Python cryptography vectors. No private key retained. Decoding is not freshness, enrollment, activation or original-write permission.',
    code=code(connection),connectionSha256=sha(canonical(connection)),legacyCode='mpa-library-v1:'+b64(canonical(legacy)),
    valid=valid,invalid=invalid,invalidCodes=invalid_codes,invalidTransitions=transitions,
    retargetCode=code(retarget),retargetHead=sign(retarget_payload,12),
    publisher=publisher,sources=sources,forgedPublisher=forged_publisher,forgedSources=forged_sources,forgedPoliciesHead=sign(forged_payload,11),revokedPublisher=revoked,revocation=sign(revocation,13),
    invalidRevocation=sign(revocation_bad,13),wrongPolicySequence=sign(wrong_sequence,11))
(directory/'library-current-control.json').write_bytes((json.dumps(result,ensure_ascii=False,indent=2)+'\n').encode('utf-8'))
print(json.dumps(dict(invalid=len(invalid),connections=len(invalid_codes),transitions=len(transitions))))
