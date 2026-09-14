"""Loopback-only, generated-data SFTP fixture. Never reads a user's SSH keys."""
import sys, socket, threading, time, os, json, base64, hashlib, stat
from pathlib import Path
sys.path.insert(0,str(Path(__file__).resolve().parent.parent/'.build/sftp-fixture-deps'))
import paramiko

root=Path(sys.argv[1]).resolve();root.mkdir(parents=True,exist_ok=True)
logs=root/'.codex/sessions';logs.mkdir(parents=True,exist_ok=True)
(root/'.codex/archived_sessions').mkdir(exist_ok=True)
id='11111111-1111-4111-8111-111111111111'
at='2026-09-14T01:00:00Z'
events=[dict(type='session_meta',timestamp=at,payload=dict(id=id,timestamp=at)),dict(type='event_msg',timestamp=at,payload=dict(type='token_count',info=dict(last_token_usage=dict(input_tokens=100,output_tokens=20))))]
(logs/('rollout-'+id+'.jsonl')).write_text(''.join(json.dumps(e)+'\n' for e in events),encoding='utf-8')
host_key=paramiko.RSAKey.generate(2048);user_key=paramiko.RSAKey.generate(2048)
user_key.write_private_key_file(str(root/'fixture-key'),password='fixture-passphrase')
class Auth(paramiko.ServerInterface):
    def get_allowed_auths(self,username):return 'password,publickey'
    def check_auth_password(self,u,p):return paramiko.AUTH_SUCCESSFUL if u=='fixture' and p=='fixture-password' else paramiko.AUTH_FAILED
    def check_auth_publickey(self,u,k):return paramiko.AUTH_SUCCESSFUL if u=='fixture' and k==user_key else paramiko.AUTH_FAILED
    def check_channel_request(self,kind,chanid):return paramiko.OPEN_SUCCEEDED if kind=='session' else paramiko.OPEN_FAILED_ADMINISTRATIVELY_PROHIBITED
class SFTP(paramiko.SFTPServerInterface):
    def local(self,path):
        p=(root/path.lstrip('/')).resolve()
        if not p.is_relative_to(root):raise PermissionError()
        return p
    def stat(self,path):
        try:return paramiko.SFTPAttributes.from_stat(self.local(path).stat())
        except OSError as e:return paramiko.SFTPServer.convert_errno(e.errno)
    lstat=stat
    def list_folder(self,path):
        try:
            rows=[]
            for p in self.local(path).iterdir():
                a=paramiko.SFTPAttributes.from_stat(p.stat());a.filename=p.name;rows.append(a)
            return rows
        except OSError as e:return paramiko.SFTPServer.convert_errno(e.errno)
    def open(self,path,flags,attr):
        if flags&(os.O_WRONLY|os.O_RDWR|os.O_CREAT|os.O_TRUNC):return paramiko.SFTP_PERMISSION_DENIED
        try:
            class ReadHandle(paramiko.SFTPHandle):
                def stat(self):return paramiko.SFTPAttributes.from_stat(os.fstat(self.readfile.fileno()))
            h=ReadHandle(flags);h.readfile=open(self.local(path),'rb');return h
        except OSError as e:return paramiko.SFTPServer.convert_errno(e.errno)
def serve(sock):
    try:
        t=paramiko.Transport(sock);t.add_server_key(host_key);t.set_subsystem_handler('sftp',paramiko.SFTPServer,SFTP);t.start_server(server=Auth())
        while t.is_active():time.sleep(.1)
        t.close()
    except (EOFError,paramiko.SSHException,OSError):pass
sock=socket.socket();sock.bind(('127.0.0.1',0));sock.listen(8)
(root/'connection.json').write_text(json.dumps(dict(Host='127.0.0.1',Port=sock.getsockname()[1],User='fixture',Directory='.codex',Fingerprint='SHA256:'+base64.b64encode(hashlib.sha256(host_key.asbytes()).digest()).decode().rstrip('='),KeyFile=str(root/'fixture-key'))),encoding='utf-8')
while True:
    s,_=sock.accept();threading.Thread(target=serve,args=(s,),daemon=True).start()
