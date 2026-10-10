"""Generic source window for the FlexCore live-preview checks.

Layout contract (framebuffer pixels, no window decorations):
banner  x=40 y=40 width=display-80 height=80  sample (display-100, 80)
entry   x=40 y=160 width=400 height=40         click (200, 180)
button  x=40 y=230 width=180 height=48         click (130, 254)
Idle banner is #c0392b. Sign in turns it #1f7a3a and prints CLICKED:<entry>.
"""
import os
import tkinter as tk

width = int(os.environ.get("FLEXCORE_PREVIEW_WIDTH", "800"))
height = int(os.environ.get("FLEXCORE_PREVIEW_HEIGHT", "600"))

root = tk.Tk()
root.overrideredirect(True)
root.geometry(f"{width}x{height}+0+0")
root.configure(bg="#f4f0e8")

banner = tk.Label(root, text="Source", bg="#c0392b", fg="white", anchor="w",
                   font=("DejaVu Sans", 24), padx=16)
banner.place(x=40, y=40, width=max(200, width - 80), height=80)

tk.Label(root, text="Username", bg="#f4f0e8", fg="#1c1c1c", anchor="w",
         font=("DejaVu Sans", 14)).place(x=40, y=132, width=200, height=24)

entry = tk.Entry(root, font=("DejaVu Sans", 18))
entry.place(x=40, y=160, width=400, height=40)


def sign_in():
    banner.configure(bg="#1f7a3a", text="Signed in: " + entry.get())
    print("CLICKED:" + entry.get(), flush=True)


button = tk.Button(root, text="Sign in", command=sign_in, bg="#1f7a3a", fg="white",
                    font=("DejaVu Sans", 16), activebackground="#1f7a3a",
                    relief="flat", borderwidth=0, highlightthickness=0)
button.place(x=40, y=230, width=180, height=48)
root.mainloop()
