// Licensed under the MIT License. See LICENSE in the project root for license information.
//
// Injects pointer/keyboard input into a KWin compositor through org_kde_kwin_fake_input.
// Intended for a private `kwin_wayland --virtual` started with KWIN_WAYLAND_NO_PERMISSION_CHECKS=1;
// it never touches /dev/uinput or the real session's devices.
//
// Commands come from argv (one per argument) or, with no arguments, from stdin (one per line):
//   move X Y          absolute pointer position in global logical coordinates
//   rel DX DY         relative pointer motion
//   down|up|click B   button: left, right, middle or an evdev code
//   wheel v|h DELTA   axis motion (KWin sends DELTA as the continuous wl_pointer.axis value)
//   keydown|keyup|tap CODE   evdev key code (KEY_A = 30)
//   symdown|symup|symtap SYM keysym (hex 0x... or decimal), resolved by KWin against its keymap
//   touchdown|touchmove ID X Y, touchup ID, touchframe   touch points in global logical coordinates
//   sleep MS
// Each command is flushed with a roundtrip, so the compositor has processed it before the next one.

#define _POSIX_C_SOURCE 200809L
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>
#include <wayland-client.h>
#include "fake-input-client-protocol.h"

static struct org_kde_kwin_fake_input *fake;
static uint32_t fake_version;

static void global_add(void *data, struct wl_registry *registry, uint32_t name, const char *interface, uint32_t version)
{
	(void)data;
	if (strcmp(interface, org_kde_kwin_fake_input_interface.name) == 0)
	{
		fake_version = version < 6 ? version : 6;
		fake = wl_registry_bind(registry, name, &org_kde_kwin_fake_input_interface, fake_version);
	}
}

static void global_remove(void *data, struct wl_registry *registry, uint32_t name)
{
	(void)data; (void)registry; (void)name;
}

static const struct wl_registry_listener registry_listener = { global_add, global_remove };

static uint32_t parse_button(const char *s)
{
	if (strcmp(s, "left") == 0) return 0x110;
	if (strcmp(s, "right") == 0) return 0x111;
	if (strcmp(s, "middle") == 0) return 0x112;
	return (uint32_t)strtoul(s, NULL, 0);
}

static void sleep_ms(long ms)
{
	struct timespec ts = { ms / 1000, (ms % 1000) * 1000000L };
	nanosleep(&ts, NULL);
}

static int need(uint32_t version, const char *cmd)
{
	if (fake_version < version)
	{
		fprintf(stderr, "uno-wl-inject: '%s' needs fake_input v%u, compositor offers v%u\n", cmd, version, fake_version);
		return 0;
	}
	return 1;
}

// Returns 0 on success, 1 on a malformed or unsupported command.
static int run(char *line)
{
	char *argv[4] = { 0 };
	int argc = 0;
	for (char *tok = strtok(line, " \t\r\n"); tok && argc < 4; tok = strtok(NULL, " \t\r\n"))
	{
		argv[argc++] = tok;
	}
	if (argc == 0 || argv[0][0] == '#')
	{
		return 0;
	}
	const char *cmd = argv[0];
	if (strcmp(cmd, "move") == 0 && argc == 3 && need(3, cmd))
	{
		org_kde_kwin_fake_input_pointer_motion_absolute(fake, wl_fixed_from_double(atof(argv[1])), wl_fixed_from_double(atof(argv[2])));
	}
	else if (strcmp(cmd, "rel") == 0 && argc == 3)
	{
		org_kde_kwin_fake_input_pointer_motion(fake, wl_fixed_from_double(atof(argv[1])), wl_fixed_from_double(atof(argv[2])));
	}
	else if ((strcmp(cmd, "down") == 0 || strcmp(cmd, "up") == 0 || strcmp(cmd, "click") == 0) && argc == 2)
	{
		uint32_t button = parse_button(argv[1]);
		if (strcmp(cmd, "up") != 0)
		{
			org_kde_kwin_fake_input_button(fake, button, WL_POINTER_BUTTON_STATE_PRESSED);
		}
		if (strcmp(cmd, "down") != 0)
		{
			org_kde_kwin_fake_input_button(fake, button, WL_POINTER_BUTTON_STATE_RELEASED);
		}
	}
	else if (strcmp(cmd, "wheel") == 0 && argc == 3)
	{
		uint32_t axis = argv[1][0] == 'h' ? WL_POINTER_AXIS_HORIZONTAL_SCROLL : WL_POINTER_AXIS_VERTICAL_SCROLL;
		org_kde_kwin_fake_input_axis(fake, axis, wl_fixed_from_double(atof(argv[2])));
	}
	else if ((strcmp(cmd, "keydown") == 0 || strcmp(cmd, "keyup") == 0 || strcmp(cmd, "tap") == 0) && argc == 2 && need(4, cmd))
	{
		uint32_t key = (uint32_t)strtoul(argv[1], NULL, 0);
		if (strcmp(cmd, "keyup") != 0)
		{
			org_kde_kwin_fake_input_keyboard_key(fake, key, WL_KEYBOARD_KEY_STATE_PRESSED);
		}
		if (strcmp(cmd, "keydown") != 0)
		{
			org_kde_kwin_fake_input_keyboard_key(fake, key, WL_KEYBOARD_KEY_STATE_RELEASED);
		}
	}
	else if ((strcmp(cmd, "symdown") == 0 || strcmp(cmd, "symup") == 0 || strcmp(cmd, "symtap") == 0) && argc == 2 && need(6, cmd))
	{
		uint32_t sym = (uint32_t)strtoul(argv[1], NULL, 0);
		if (strcmp(cmd, "symup") != 0)
		{
			org_kde_kwin_fake_input_keyboard_keysym(fake, sym, WL_KEYBOARD_KEY_STATE_PRESSED);
		}
		if (strcmp(cmd, "symdown") != 0)
		{
			org_kde_kwin_fake_input_keyboard_keysym(fake, sym, WL_KEYBOARD_KEY_STATE_RELEASED);
		}
	}
	else if (strcmp(cmd, "touchdown") == 0 && argc == 4 && need(2, cmd))
	{
		org_kde_kwin_fake_input_touch_down(fake, (uint32_t)strtoul(argv[1], NULL, 0), wl_fixed_from_double(atof(argv[2])), wl_fixed_from_double(atof(argv[3])));
	}
	else if (strcmp(cmd, "touchmove") == 0 && argc == 4 && need(2, cmd))
	{
		org_kde_kwin_fake_input_touch_motion(fake, (uint32_t)strtoul(argv[1], NULL, 0), wl_fixed_from_double(atof(argv[2])), wl_fixed_from_double(atof(argv[3])));
	}
	else if (strcmp(cmd, "touchup") == 0 && argc == 2 && need(2, cmd))
	{
		org_kde_kwin_fake_input_touch_up(fake, (uint32_t)strtoul(argv[1], NULL, 0));
	}
	else if (strcmp(cmd, "touchframe") == 0 && need(2, cmd))
	{
		org_kde_kwin_fake_input_touch_frame(fake);
	}
	else if (strcmp(cmd, "sleep") == 0 && argc == 2)
	{
		sleep_ms(atol(argv[1]));
	}
	else
	{
		fprintf(stderr, "uno-wl-inject: bad command '%s'\n", cmd);
		return 1;
	}
	return 0;
}

int main(int argc, char **argv)
{
	struct wl_display *display = wl_display_connect(NULL);
	if (!display)
	{
		fprintf(stderr, "uno-wl-inject: cannot connect to WAYLAND_DISPLAY\n");
		return 2;
	}
	struct wl_registry *registry = wl_display_get_registry(display);
	wl_registry_add_listener(registry, &registry_listener, NULL);
	wl_display_roundtrip(display);
	if (!fake)
	{
		fprintf(stderr, "uno-wl-inject: org_kde_kwin_fake_input not advertised (KWin needs KWIN_WAYLAND_NO_PERMISSION_CHECKS=1 or an X-KDE-Wayland-Interfaces grant)\n");
		return 3;
	}
	org_kde_kwin_fake_input_authenticate(fake, "uno-wl-inject", "Automated Uno Wayland input tests");
	// KWin creates the fake device on bind; give the seat a moment to announce capabilities.
	wl_display_roundtrip(display);

	int failures = 0;
	char buffer[256];
	if (argc > 1)
	{
		for (int i = 1; i < argc; i++)
		{
			snprintf(buffer, sizeof buffer, "%s", argv[i]);
			failures += run(buffer);
			if (wl_display_roundtrip(display) < 0)
			{
				return 4;
			}
		}
	}
	else
	{
		while (fgets(buffer, sizeof buffer, stdin))
		{
			failures += run(buffer);
			if (wl_display_roundtrip(display) < 0)
			{
				return 4;
			}
		}
	}
	if (fake_version >= 5)
	{
		org_kde_kwin_fake_input_destroy(fake);
	}
	wl_display_roundtrip(display);
	wl_display_disconnect(display);
	return failures ? 1 : 0;
}
