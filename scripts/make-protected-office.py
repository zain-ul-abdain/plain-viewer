# Saves password-protected copies of this corpus's own documents with the development LibreOffice, through UNO.
# Run by scripts/make-protected-office.ps1 with LibreOffice's own Python; arguments: <pipe name> then pairs of
# <source path> <target path> <filter name>. The password is the corpus's test password, "viewer-test".
import sys
import time
import uno
from com.sun.star.beans import PropertyValue


def prop(name, value):
    p = PropertyValue()
    p.Name = name
    p.Value = value
    return p


def main():
    pipe = sys.argv[1]
    jobs = sys.argv[2:]
    local = uno.getComponentContext()
    resolver = local.ServiceManager.createInstanceWithContext("com.sun.star.bridge.UnoUrlResolver", local)
    context = None
    for _ in range(120):
        try:
            context = resolver.resolve(f"uno:pipe,name={pipe};urp;StarOffice.ComponentContext")
            break
        except Exception:
            time.sleep(0.5)
    if context is None:
        raise SystemExit("LibreOffice did not start")
    desktop = context.ServiceManager.createInstanceWithContext("com.sun.star.frame.Desktop", context)
    try:
        for i in range(0, len(jobs), 3):
            source, target, filter_name = jobs[i:i + 3]
            document = desktop.loadComponentFromURL(uno.systemPathToFileUrl(source), "_blank", 0,
                                                    (prop("Hidden", True), prop("ReadOnly", True), prop("UpdateDocMode", 0)))
            document.storeToURL(uno.systemPathToFileUrl(target),
                                (prop("FilterName", filter_name), prop("Password", "viewer-test"), prop("Overwrite", True)))
            document.close(True)
            print("made    " + target)
    finally:
        try:
            desktop.terminate()
        except Exception:
            pass


main()
