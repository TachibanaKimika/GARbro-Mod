"""Synthetic, secret-free regression cases for explicit YPF parameters.
Run: python tests/Cli/test_local_parameters.py [Debug|Release]
"""
import json,os,struct,subprocess,sys,tempfile
from pathlib import Path

ROOT=Path(__file__).resolve().parents[2]
CLI=ROOT/'bin'/(sys.argv[1] if len(sys.argv)>1 else 'Debug')/'Onachi-GARbro.Cli.exe'

def call(args,env):
    p=subprocess.run([str(CLI),*map(str,args),'--output','json','--non-interactive'],env=env,capture_output=True,encoding='utf8')
    return json.loads(p.stdout)

def fixture(path,high=0,packed=False):
    name=b'fixture.bin';payload=b'\x03\x08abc' if packed else b'abc'
    end=32+5+len(name)+22
    index=struct.pack('<IB',0,len(name)^255)+name+struct.pack('<BBIIQI',0,packed,3,len(payload),end+(high<<32),0)
    path.write_bytes(b'YPF\0'+struct.pack('<III',555,1,len(index))+bytes(16)+index+payload)

def main():
    with tempfile.TemporaryDirectory(prefix='garbro-ypf-test-') as temporary:
        root=Path(temporary);archive=root/'test.ypf';config=root/'parameters.json'
        params=dict(archive_directory=str(root),name_xor=0,swap_table=[],script_key=0,compression='zlib')
        config.write_text(json.dumps(params));env=dict(os.environ,GARBRO_YPF_PARAMETERS=str(config))
        fixture(archive)
        assert call(['archive','list',archive],env)['status']=='success'
        fixture(archive,high=1)
        assert call(['archive','list',archive],env)['status']!='success','64-bit offset was truncated'
        fixture(archive,packed=True);params['compression']='snappy';config.write_text(json.dumps(params))
        args=['archive','extract',archive,'--destination',root/'out','--budget','auto','--checksum','none','--overwrite','never']
        assert call(args+['--dry-run'],env)['status']=='success'
        assert call(args,env)['status']=='success'
        assert (root/'out/fixture.bin').read_bytes()==b'abc','Snappy must work with script_key=0'
        params['archive_directory']=str(root/'other');config.write_text(json.dumps(params))
        assert call(['archive','list',archive],env)['status']!='success','scope was ignored'
        params['archive_directory']=str(root);params['swap_table']=[1,2,1,3];config.write_text(json.dumps(params))
        assert call(['archive','list',archive],env)['status']!='success','duplicate swap accepted'
    print('PASS: valid index, 64-bit rejection, zero-key Snappy, exact scope, duplicate swaps')

if __name__=='__main__':main()
