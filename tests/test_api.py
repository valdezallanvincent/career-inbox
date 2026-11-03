"""Real HTTP + SQLite integration tests. Uses only Python's standard library."""
import concurrent.futures
from contextlib import closing
import json
import os
from pathlib import Path
import socket
import sqlite3
import subprocess
import tempfile
import time
import unittest
import urllib.error
import urllib.request

ROOT = Path(__file__).resolve().parents[1]

class ApiTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.temp = tempfile.TemporaryDirectory()
        with socket.socket() as sock:
            sock.bind(('127.0.0.1', 0))
            cls.port = sock.getsockname()[1]
        cls.base = f'http://127.0.0.1:{cls.port}'
        env = dict(os.environ, CAREER_INBOX_DATA=cls.temp.name, CAREER_PORT=str(cls.port), CAREER_MIN_HOURLY_USD='10')
        cls.process = subprocess.Popen(['dotnet', str(ROOT/'src/CareerInbox/bin/Release/net8.0/CareerInbox.dll')], env=env, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        for _ in range(100):
            try:
                urllib.request.urlopen(cls.base+'/health', timeout=1)
                return
            except OSError:
                time.sleep(.1)
        cls.process.terminate()
        raise RuntimeError('API did not start')

    @classmethod
    def tearDownClass(cls):
        cls.process.terminate()
        cls.process.wait(timeout=10)
        cls.temp.cleanup()

    def call(self, path, body=None, headers=None):
        request = urllib.request.Request(self.base+path, data=None if body is None else json.dumps(body).encode(), headers=headers or {'Content-Type':'application/json'})
        try:
            with urllib.request.urlopen(request, timeout=10) as response:
                raw = response.read().decode()
                return response.status, json.loads(raw) if response.headers.get_content_type()=='application/json' else raw
        except urllib.error.HTTPError as error:
            raw=error.read().decode()
            code=error.code
            error.close()
            return code, raw

    def job(self, suffix, **overrides):
        return dict(url='https://example.com/jobs/'+suffix, title='C# .NET Azure Developer', company='Fictional Demo Co', description='SQL Server REST APIs Angular Docker CI/CD automation', hourlyUsd=20, eligibility='yes', vacancyStatus='open', **overrides)

    def test_validation_and_no_invalid_save(self):
        before=len(self.call('/jobs')[1])
        for field, value in [('title','   '),('description',''),('url','file:///secret'),('url','http://localhost/private'),('hourlyUsd',-1),('eligibility','maybe')]:
            item=self.job('bad'); item[field]=value
            self.assertEqual(self.call('/jobs',item)[0],400)
        self.assertEqual(len(self.call('/jobs')[1]),before)

    def test_duplicate_tracking_urls_and_concurrent_retries(self):
        item=self.job('same'); item['url']+='?utm_source=email'
        first=self.call('/jobs',item)[1]
        item['url']='https://example.com/jobs/same?utm_source=linkedin#top'
        with concurrent.futures.ThreadPoolExecutor(max_workers=5) as pool:
            results=list(pool.map(lambda _:self.call('/jobs',item)[1],range(5)))
        self.assertTrue(all(x['duplicate'] and x['id']==first['id'] for x in results))

    def test_distinct_job_query_ids_are_preserved(self):
        a=self.job('query'); a['url']+='?job=1'
        b=self.job('query'); b['url']+='?job=2'
        self.assertNotEqual(self.call('/jobs',a)[1]['id'],self.call('/jobs',b)[1]['id'])

    def test_scoring_exclusions_unknown_and_closed(self):
        self.assertEqual(self.call('/jobs',self.job('strong'))[1]['score'],100)
        for suffix, changes in [('low',{'hourlyUsd':9}),('closed',{'description':'This job has been closed'}),('geo',{'eligibility':'no'})]:
            item=self.job(suffix); item.update(changes); result=self.call('/jobs',item)[1]
            saved=next(x for x in self.call('/jobs')[1] if x['id']==result['id'])
            self.assertEqual(saved['status'],'excluded')
        item=self.job('unknown'); item.update(hourlyUsd=None,vacancyStatus='unverified',eligibility='unknown')
        result=self.call('/jobs',item)[1]
        self.assertIn('unknown',result['message']); self.assertIn('unverified',result['message'])

    def test_status_audit_and_followup(self):
        result=self.call('/jobs',self.job('followup'))[1]
        self.assertEqual(self.call('/status',{'id':result['id'],'status':'applied','note':'Applied manually'})[0],200)
        self.assertEqual(self.call('/status',{'id':result['id'],'status':'made-up'})[0],400)
        self.assertEqual(self.call('/status',{'id':999999,'status':'applied'})[0],400)
        with closing(sqlite3.connect(Path(self.temp.name)/'inbox.db')) as db:
            self.assertEqual(db.execute('SELECT count(*) FROM events WHERE job_id=?',(result['id'],)).fetchone()[0],2)
            db.execute("UPDATE jobs SET updated_at='2020-01-01T00:00:00Z' WHERE id=?",(result['id'],))
            db.commit()
        self.assertIn(f"#{result['id']}:",self.call('/digest')[1].split('Follow-ups due')[1])

    def test_digest_is_repeatable_and_does_not_create_jobs(self):
        before=len(self.call('/jobs')[1])
        for _ in range(2): self.assertEqual(self.call('/digest',{})[0],200)
        self.assertTrue((Path(self.temp.name)/'latest-shortlist.md').exists())
        self.assertEqual(len(self.call('/jobs')[1]),before)

    def test_browser_cross_origin_mutation_blocked(self):
        self.assertEqual(self.call('/jobs',self.job('origin'),{'Content-Type':'application/json','Origin':'https://example.com'})[0],403)
        self.assertEqual(self.call('/jobs',self.job('host'),{'Content-Type':'application/json','Host':'evil.example'})[0],403)

if __name__=='__main__': unittest.main(verbosity=2)
