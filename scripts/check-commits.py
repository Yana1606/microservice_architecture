"""Verify the per-commit file limit and keep generated artifacts out of Git."""
import argparse,subprocess,sys
from pathlib import PurePosixPath
parser=argparse.ArgumentParser()
scope=parser.add_mutually_exclusive_group()
scope.add_argument('--staged',action='store_true')
scope.add_argument('--ref',help='Check commits reachable from this revision instead of every ref; use the PR head SHA in CI.')
args=parser.parse_args()
def git(*values):return subprocess.check_output(['git',*values])
failed=False
if args.staged:
    groups=[('staged',git('diff','--cached','--name-only','-z').split(b'\0'))]
else:
    groups=[]
    for commit in git('rev-list',args.ref or '--all').decode().split():
        parents=git('rev-list','--parents','-n','1',commit).decode().split()
        names=git('diff','--name-only','-z',parents[1],commit) if len(parents)>1 else git('diff-tree','--root','--no-commit-id','--name-only','-r','-z',commit)
        groups.append((commit[:8],names.split(b'\0')))
for commit,names in groups:
    paths=[p.decode('utf-8') for p in names if p];print(commit+': '+str(len(paths))+' changed files')
    if args.staged:
        for path in paths:print('  '+path)
    if len(paths)>50:
        print('ERROR: '+commit+' exceeds the limit of 50 changed files');failed=True
for raw in git('ls-files','-z').split(b'\0'):
    if not raw:continue
    path=PurePosixPath(raw.decode('utf-8'))
    if any(part in {'bin','obj','data','logs','test-results','__pycache__'} or part.startswith('browser-') for part in path.parts) or path.suffix in {'.db','.sqlite','.sqlite3','.log'}:
        print('Tracked generated artifact: '+str(path));failed=True
sys.exit(1 if failed else 0)
