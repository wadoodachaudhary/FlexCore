"""Generic target window for the FlexCore live-preview checks.

Same control positions as source_form.py. Idle banner is #1d4e89.
Sign in turns it #e67e22 and prints CLICKED:<entry>.
"""
import os
import tkinter as tk

width = int(os.environ.get("FLEXCORE_PREVIEW_WIDTH", "800"))
height = int(os.environ.get("FLEXCORE_PREVIEW_HEIGHT", "600"))

root = tk.Tk()
root.overrideredirect(True)
root.geometry(f"{width}x{height}+0+0")
root.configure(bg="#d9e8f5")

banner = tk.Label(root, text="Target", bg="#1d4e89", fg="white", anchor="w",
                   font=("DejaVu Sans", 24), padx=16)
banner.place(x=40, y=40, width=max(200, width - 80), height=80)

tk.Label(root, text="Username", bg="#d9e8f5", fg="#1c1c1c", anchor="w",
         font=("DejaVu Sans", 14)).place(x=40, y=132, width=200, height=24)

entry = tk.Entry(root, font=("DejaVu Sans", 18))
entry.place(x=40, y=160, width=400, height=40)


def sign_in():
    banner.configure(bg="#e67e22", text="Signed in: " + entry.get())
    print("CLICKED:" + entry.get(), flush=True)


button = tk.Button(root, text="Sign in", command=sign_in, bg="#1d4e89", fg="white",
                    font=("DejaVu Sans", 16), activebackground="#1d4e89",
                    relief="flat", borderwidth=0, highlightthickness=0)
button.place(x=40, y=230, width=180, height=48)
root.mainloop()
