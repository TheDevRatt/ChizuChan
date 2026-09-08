"""Offline subprocess fixture. Never contacts media services or reads application config."""
import json
import os
from pathlib import Path
import subprocess
import sys
import threading
import time

mode = sys.argv[1]
if mode == "output":
    count = int(sys.argv[2])
    stream = sys.argv[3]
    marker = Path(sys.argv[4])

    def emit(pipe):
        remaining = count
        while remaining:
            size = min(4096, remaining)
            pipe.write("p" * size)
            pipe.flush()
            remaining -= size

    pipes = [sys.stdout, sys.stderr] if stream == "both" else [getattr(sys, stream)]
    threads = [threading.Thread(target=emit, args=(pipe,)) for pipe in pipes]
    for thread in threads:
        thread.start()
    for thread in threads:
        thread.join()
    marker.write_text("finished", encoding="utf-8")
elif mode == "metadata":
    # Truncation to the capture bound would leave a valid JSON object plus whitespace.
    sys.stdout.write('{"duration":25200}' + ' ' * 131072)
    sys.stdout.flush()
elif mode == "healthy":
    for _ in range(6):
        print("progress", flush=True)
        time.sleep(0.05)
    Path(sys.argv[2]).write_text("finished", encoding="utf-8")
elif mode == "tree":
    child = subprocess.Popen([sys.executable, "-c", "import time; time.sleep(60)"])
    Path(sys.argv[2]).write_text(json.dumps([os.getpid(), child.pid]), encoding="utf-8")
    child.wait()
elif mode == "sleep":
    time.sleep(60)
else:
    raise ValueError("Unknown fixture mode")
