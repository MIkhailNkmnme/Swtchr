#!/usr/bin/env python3
"""Swtchr for Debian/XFCE/X11 — experimental manual layout correction.

Ctrl+Shift (press and release with no other key): word -> line -> restore.
No text is persisted or sent anywhere. Requires an X11 session.
"""

import argparse
import logging
import queue
import subprocess
import sys
import threading
import time

EN_L = "\`1234567890-=qwertyuiop[]\\asdfghjkl;'zxcvbnm,./"
RU_L = "ё1234567890-=йцукенгшщзхъ\\фывапролджэячсмитьбю."
EN_U = '~!@#$%^&*()_+QWERTYUIOP{}|ASDFGHJKL:"ZXCVBNM<>?'
RU_U = 'Ё!"№;%:?*()_+ЙЦУКЕНГШЩЗХЪ/ФЫВАПРОЛДЖЭЯЧСМИТЬБЮ,'
EN_TO_RU = dict(zip(EN_L + EN_U, RU_L + RU_U))
RU_TO_EN = dict(zip(RU_L + RU_U, EN_L + EN_U))
EN_LETTERS = frozenset("qwertyuiopasdfghjklzxcvbnmQWERTYUIOPASDFGHJKLZXCVBNM")
RU_LETTERS = frozenset("йцукенгшщзхъфывапролджэячсмитьбюёЙЦУКЕНГШЩЗХЪФЫВАПРОЛДЖЭЯЧСМИТЬБЮЁ")


def convert_layout(source):
    """Port of the physical-layout conversion rules from the C# version."""
    output = []
    previous = 0
    for index, character in enumerate(source):
        if character in EN_LETTERS:
            output.append(EN_TO_RU.get(character, character))
            previous = 1
        elif character in RU_LETTERS:
            output.append(RU_TO_EN.get(character, character))
            previous = 2
        else:
            direction = previous
            if not direction:
                for next_char in source[index + 1:]:
                    if next_char in EN_LETTERS:
                        direction = 1
                        break
                    if next_char in RU_LETTERS:
                        direction = 2
                        break
            if direction == 1:
                output.append(EN_TO_RU.get(character, character))
            elif direction == 2:
                output.append(RU_TO_EN.get(character, character))
            else:
                output.append(character)
    return "".join(output)


def command(*args, timeout=3):
    return subprocess.run(args, check=True, stdout=subprocess.PIPE,
                          stderr=subprocess.PIPE, timeout=timeout)


def xkey(keys):
    command("xdotool", "key", "--clearmodifiers", "--delay", "2", keys)
    time.sleep(0.07)


def xtype(text):
    command("xdotool", "type", "--clearmodifiers", "--delay", "2", "--", text,
            timeout=max(4, len(text) * 0.03))
    time.sleep(0.09)


def selection():
    try:
        raw = command("xclip", "-selection", "primary", "-o").stdout
        return raw.decode("utf-8")
    except (subprocess.CalledProcessError, UnicodeDecodeError, subprocess.TimeoutExpired):
        return ""


def focus():
    try:
        return command("xdotool", "getwindowfocus").stdout.decode().strip()
    except (subprocess.CalledProcessError, subprocess.TimeoutExpired):
        return ""


def focus_class():
    try:
        return command("xdotool", "getwindowfocus", "getwindowclassname").stdout.decode().strip().lower()
    except (subprocess.CalledProcessError, subprocess.TimeoutExpired):
        return ""


# The first version avoids terminal input (Ctrl+C or selection would be unsafe).
EXCLUDED = ("terminal", "xterm", "kitty", "alacritty", "konsole", "wezterm", "urxvt")


class Corrector:
    def __init__(self):
        self.step = 0
        self.window = ""
        self.original_line = ""

    def reset(self):
        self.step = 0
        self.window = ""
        self.original_line = ""

    def selected(self, key):
        """Select with keyboard and read X11 PRIMARY (clipboard untouched)."""
        xkey(key)
        return selection()

    def replace(self, replacement):
        if not replacement:
            return False
        xtype(replacement)
        return True

    def activate(self):
        current = focus()
        if not current:
            return
        if any(term in focus_class() for term in EXCLUDED):
            logging.info("Terminal window is excluded in the initial release")
            self.reset()
            return
        if self.step and current != self.window:
            self.reset()
        if self.step == 0:
            self.window = current
            # Save the portion of the line before the caret.
            self.original_line = self.selected("shift+Home")
            xkey("Right")
            if not self.original_line or "\n" in self.original_line:
                self.reset()
                return
            word = self.selected("ctrl+shift+Left")
            if not word or not self.original_line.rstrip().endswith(word.strip()):
                xkey("Right")
                self.reset()
                return
            changed = convert_layout(word)
            if changed == word:
                xkey("Right")
                self.reset()
                return
            self.replace(changed)
            self.step = 1
            logging.info("Word converted; next Ctrl+Shift converts the line")
        elif self.step == 1:
            currently = self.selected("shift+Home")
            if not currently:
                self.reset()
                return
            changed = convert_layout(self.original_line)
            if changed == currently:
                xkey("Right")
                self.reset()
                return
            self.replace(changed)
            self.step = 2
            logging.info("Line converted; next Ctrl+Shift restores original")
        else:
            currently = self.selected("shift+Home")
            if currently:
                self.replace(self.original_line)
            self.reset()
            logging.info("Original line restored")


class Listener:
    def __init__(self):
        from Xlib import XK, display
        self.display = display.Display()
        self.record_display = display.Display()
        self.ctrl = self.keycodes(XK, "Control_L", "Control_R")
        self.shift = self.keycodes(XK, "Shift_L", "Shift_R")
        self.held = set()
        self.armed = False
        self.combo = False
        self.dirty = False
        self.busy = False
        self.ignore_until = 0.0
        self.jobs = queue.Queue()
        self.corrector = Corrector()

    def keycodes(self, XK, *names):
        return {self.display.keysym_to_keycode(XK.string_to_keysym(n))
                for n in names} - {0}

    def run_job(self):
        while True:
            self.jobs.get()
            self.busy = True
            try:
                time.sleep(0.16)  # Let the user release modifiers.
                self.corrector.activate()
            except Exception:
                logging.exception("Conversion failed")
                self.corrector.reset()
            finally:
                self.ignore_until = time.monotonic() + 0.45
                self.busy = False
                self.jobs.task_done()

    def handle(self, reply):
        from Xlib import X
        from Xlib.protocol import rq
        if reply.category != 0 or not reply.data or self.busy:
            return
        data = reply.data
        while data:
            event, data = rq.EventField(None).parse_binary_value(
                data, self.record_display.display, None, None)
            if time.monotonic() < self.ignore_until:
                continue
            key = event.detail
            if event.type == X.ButtonPress:
                self.armed = False
                self.dirty = True
                self.corrector.reset()
            elif event.type == X.KeyPress:
                if key in self.ctrl | self.shift:
                    self.held.add(key)
                    if (self.held & self.ctrl) and (self.held & self.shift) and not self.dirty:
                        self.combo = True
                        self.armed = True
                else:
                    self.dirty = True
                    self.armed = False
                    self.corrector.reset()
            elif event.type == X.KeyRelease and key in self.ctrl | self.shift:
                was_armed = self.armed and self.combo
                self.held.discard(key)
                if was_armed and not ((self.held & self.ctrl) and (self.held & self.shift)):
                    self.armed = False
                    self.combo = False
                    if self.jobs.empty():
                        self.jobs.put(True)
                if not self.held:
                    self.armed = False
                    self.combo = False
                    self.dirty = False

    def run(self):
        from Xlib import X
        from Xlib.ext import record
        if not self.record_display.has_extension("RECORD"):
            raise RuntimeError("X11 RECORD extension unavailable")
        threading.Thread(target=self.run_job, daemon=True).start()
        context = self.record_display.record_create_context(
            0, [record.AllClients], [{
                "core_requests": (0, 0), "core_replies": (0, 0),
                "ext_requests": (0, 0), "ext_replies": (0, 0),
                "delivered_events": (0, 0),
                "device_events": (X.KeyPress, X.ButtonPress),
                "errors": (0, 0), "client_started": False, "client_died": False,
            }])
        logging.info("Swtchr X11 running: Ctrl+Shift -> word / line / restore")
        try:
            self.record_display.record_enable_context(context, self.handle)
        finally:
            self.record_display.record_free_context(context)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--convert", metavar="TEXT", help="Test layout conversion without X11")
    args = parser.parse_args()
    if args.convert is not None:
        print(convert_layout(args.convert))
        return
    import os
    if os.environ.get("XDG_SESSION_TYPE", "").lower() != "x11":
        sys.exit("This prototype requires an X11 session (echo $XDG_SESSION_TYPE).")
    logging.basicConfig(level=logging.INFO, format="[Swtchr] %(message)s")
    try:
        Listener().run()
    except KeyboardInterrupt:
        pass


if __name__ == "__main__":
    main()
