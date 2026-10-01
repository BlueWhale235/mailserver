import json,os,subprocess,sys,tarfile
from pathlib import Path
work=Path('/mnt/storage/mailserver-burst-20261001');private=json.loads((work/'private.json').read_text());database='mailserver_acceptance_burst_20261001'
subprocess.run(['docker','exec','-i','1Panel-mysql','sh','-c','MYSQL_PWD="$MYSQL_ROOT_PASSWORD" mysql -uroot'],input=("CREATE DATABASE IF NOT EXISTS `"+database+"`; GRANT ALL ON `"+database+"`.* TO 'app-mailserver'@'%';").encode(),check=True,stdout=subprocess.DEVNULL)
with tarfile.open(work/'tests.tar.gz') as t:t.extractall(work/'regression',filter='data')
env=os.environ.copy();env['DOTNET_ROOT']='/mnt/storage/mailserver-evaluation-20261001/sdk';env['MAILSERVER_TEST_MYSQL_CONNECTION']='Server=127.0.0.1;Port=3306;Database='+database+';User ID=app-mailserver;Password='+private['databasePassword']+';SslMode=Disabled'
result=subprocess.run([env['DOTNET_ROOT']+'/dotnet','vstest',str(work/'regression/MailServer.Tests.dll'),'--logger:trx;LogFileName=burst-full.trx','--ResultsDirectory:'+str(work/'regression-results')],env=env,cwd=work/'regression');sys.exit(result.returncode)
