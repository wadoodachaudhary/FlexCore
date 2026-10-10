"""Line-oriented console fixture. Prints ready, then echoes one stdin line."""
import sys

print("ready", flush=True)
line = sys.stdin.readline()
print("got:" + line.rstrip("\n"), flush=True)
