"""Verify signed MSIX payloads, block hashes and transparent shell resources."""
import argparse
import base64
import hashlib
import io
import json
from pathlib import Path
import urllib.parse
import xml.etree.ElementTree as ET
import zipfile

from PIL import Image


def sha256(data):
    return base64.b64encode(hashlib.sha256(data).digest()).decode("ascii")


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument("package",type=Path)
    parser.add_argument("--version",required=True)
    parser.add_argument("--release",type=Path,default=Path("artifacts/CX-Printer-win-x64"))
    parser.add_argument("--report",type=Path)
    args=parser.parse_args()
    with zipfile.ZipFile(args.package) as archive:
        lookup={urllib.parse.unquote(name).replace("\\","/"):name for name in archive.namelist()}
        manifest=ET.fromstring(archive.read("AppxManifest.xml"))
        identity=manifest.find("{*}Identity").attrib
        assert identity["Version"]==args.version
        assert identity["Publisher"]=="CN=MasterTea"
        family=manifest.find("{*}Dependencies/{*}TargetDeviceFamily").attrib
        assert family["MinVersion"]=="10.0.17763.0"
        visuals=manifest.find("{*}Applications/{*}Application/{*}VisualElements")
        assert visuals.attrib["BackgroundColor"]=="transparent"
        for name in ("CX Printer.dll","CxPrint.Core.dll","CxPrint.Windows.dll",
                     "Bridge/CxPrint.Service.dll","Bridge/CxPrint.Windows.dll"):
            assert archive.read(lookup[name])==(args.release/name).read_bytes(),name+" payload mismatch"
        assert "resources.pri" in lookup and "AppxSignature.p7x" in lookup
        assert not any("DebugData" in n or "DebugUI" in n or n.endswith((".pfx",".password")) for n in lookup)
        icons=[n for n in lookup if n.startswith("PackageAssets/") and n.endswith(".png")]
        assert len(icons)==51,len(icons)
        for name in icons:
            with Image.open(io.BytesIO(archive.read(lookup[name]))) as image:
                assert image.convert("RGBA").getchannel("A").getextrema()[0]==0,name
        files=blocks=0
        for entry in ET.fromstring(archive.read("AppxBlockMap.xml")):
            name=entry.attrib["Name"].replace("\\","/")
            data=archive.read(lookup[name])
            assert len(data)==int(entry.attrib["Size"]),name
            # Current SDKs can append a 2021 FileHash element after the 64 KiB blocks.
            for index,block in enumerate(entry.findall("{*}Block")):
                assert sha256(data[index*65536:(index+1)*65536])==block.attrib["Hash"],name
                blocks+=1
            for file_hash in entry.findall("{*}FileHash"):
                assert sha256(data)==file_hash.attrib["Hash"],name
            files+=1
    with args.package.open("rb") as stream:
        checksum=hashlib.file_digest(stream,"sha256").hexdigest()
    assert checksum==Path(str(args.package)+".sha256").read_text().split()[0],"Checksum mismatch"
    result={"identity":identity,"target_device_family":family,"verified_files":files,
            "verified_blocks":blocks,"transparent_icon_assets":len(icons),"current_payloads_verified":True,
            "development_data":False,"private_key_in_package":False,"sha256":checksum,
            "size_bytes":args.package.stat().st_size}
    if args.report:
        args.report.write_text(json.dumps(result,indent=2),encoding="utf-8")
    print(json.dumps(result,indent=2))


if __name__=="__main__": main()
