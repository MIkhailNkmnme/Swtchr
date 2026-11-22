#!/usr/bin/env python3
"""Swtchr for Debian/XFCE/X11 — experimental manual layout correction.

Ctrl+Shift (press and release with no other key): one more word each time -> restore.
No text is persisted or sent anywhere. Requires an X11 session.
"""

import argparse
import logging
import queue
import subprocess
import sys
import threading
import time

EN_L = "`1234567890-=qwertyuiop[]\\asdfghjkl;'zxcvbnm,./"
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
    """Grow a corrected suffix by one word with every press, then restore."""

    def __init__(self):
        self.reset()

    def reset(self):
        self.count = 0
        self.window = ""
        self.original_line = ""
        self.expected_line = ""
        self.previous_span = 0
        self.reached_start = False

    def select(self, keys):
        """Select text and read X11 PRIMARY without touching the clipboard."""
        xkey(keys)
        time.sleep(0.13)  # Give X11 PRIMARY selection owner time to update.
        return selection()

    def read_line(self):
        # Highlight from caret to beginning of the current logical line.
        line = self.select("shift+Home")
        # Move to the right edge and remove the selection.
        xkey("Right")
        return line

    def restore(self):
        selected = self.select("shift+Home")
        if selected != self.expected_line:
            xkey("Right")
            self.reset()
            logging.warning("Text changed; refusing to restore")
            return
        xtype(self.original_line)
        self.reset()
        logging.info("Original line restored")

    def activate(self):
        logging.info("Ctrl+Shift recognized; attempting conversion")
        current = focus()
        if not current:
            logging.warning("Cannot find focused window")
            self.reset()
            return
        if any(term in focus_class() for term in EXCLUDED):
            logging.info("Terminals are excluded from this prototype")
            self.reset()
            return

        if self.count and current != self.window:
            self.reset()

        line = self.read_line()
        if not line or "\n" in line:
            logging.warning("No suitable line selected (caret must be after text)")
            self.reset()
            return

        if self.count and line != self.expected_line:
            logging.info("Text or caret changed; starting a new correction cycle")
            self.reset()

        if self.count and self.reached_start:
            self.restore()
            return

        if not self.count:
            self.window = current
            self.original_line = line
            self.expected_line = line

        # Selecting from the right edge, Ctrl+Shift+Left expands the suffix
        # by precisely one more word on each invocation (editor word rules).
        for _ in range(self.count + 1):
            xkey("ctrl+shift+Left")
        suffix_now = selection()

        if not suffix_now or len(suffix_now) > len(line) or not line.endswith(suffix_now):
            xkey("Right")
            self.reset()
            logging.warning("Cannot safely identify selected word")
            return

        if self.count and len(suffix_now) <= self.previous_span:
            # Some editors stop growing before they reach the beginning.
            xkey("Right")
            self.restore()
            return

        # Always recompute from the original, never re-convert already corrected
        # words, otherwise each repeated press would flip them back.
        suffix_original = self.original_line[-len(suffix_now):]
        corrected = convert_layout(suffix_original)
        if corrected == suffix_now:
            xkey("Right")
            logging.info("No characters to convert in selected word")
            self.reset()
            return

        xtype(corrected)  # Replaces only the highlighted suffix.
        self.count += 1
        self.previous_span = len(suffix_now)
        self.expected_line = line[:-len(suffix_now)] + corrected
        self.reached_start = len(suffix_now) == len(line)
        logging.info("Corrected %d word(s)%s", self.count,
                     "; next press restores" if self.reached_start else "")

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
                self.ignore_until = time.monotonic() + 0.12
                self.busy = False
                self.jobs.task_done()

    def handle(self, reply):
        from Xlib import X
        from Xlib.protocol import rq
        if reply.category != 0 or not reply.data:
            return
        data = reply.data
        while data:
            event, data = rq.EventField(None).parse_binary_value(
                data, self.record_display.display, None, None)
            key = event.detail
            # Even while converting, observe modifier releases so held-state
            # does not get stuck after the first Ctrl+Shift activation.
            if self.busy or time.monotonic() < self.ignore_until:
                if event.type == X.KeyRelease and key in self.ctrl | self.shift:
                    self.held.discard(key)
                    if not self.held:
                        self.armed = self.combo = self.dirty = False
                continue
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
                "ext_requests": (0, 0, 0, 0),
                "ext_replies": (0, 0, 0, 0),
                "delivered_events": (0, 0),
                "device_events": (X.KeyPress, X.ButtonPress),
                "errors": (0, 0), "client_started": False, "client_died": False,
            }])
        logging.info("Swtchr X11 running: Ctrl+Shift -> one more word per press -> restore")
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
