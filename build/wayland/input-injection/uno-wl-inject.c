// Licensed under the MIT License. See LICENSE in the project root for license information.
//
// Injects pointer/keyboard input into a KWin compositor through org_kde_kwin_fake_input.
// Intended for a private `kwin_wayland --virtual` started with KWIN_WAYLAND_NO_PERMISSION_CHECKS=1;
// Requires --socket NAME or UNO_WL_INJECT_SOCKET; session socket names need --allow-session.
// Never uses WAYLAND_DISPLAY or WAYLAND_SOCKET and never touches /dev/uinput.
//
// Commands follow options in argv (one per argument), or come from stdin (one per line).
// Use -- to end options. Commands are limited to 255 bytes (excluding the newline).
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
#include <errno.h>
#include <math.h>
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

static int parse_uint(const char *s, uint32_t *value)
{
	char *end;
	errno = 0;
	unsigned long parsed = strtoul(s, &end, 0);
	if (!*s || *s == '-' || *end || errno || parsed > UINT32_MAX) return 0;
	*value = (uint32_t)parsed;
	return 1;
}

static int parse_coord(const char *s, double *value)
{
	char *end;
	errno = 0;
	*value = strtod(s, &end);
	// Wayland coordinates are signed 24.8 fixed point.
	return *s && !*end && !errno && isfinite(*value)
		&& *value >= -8388608.0 && *value <= 8388607.99609375;
}

static int parse_button(const char *s, uint32_t *value)
{
	if (strcmp(s, "left") == 0) *value = 0x110;
	else if (strcmp(s, "right") == 0) *value = 0x111;
	else if (strcmp(s, "middle") == 0) *value = 0x112;
	else return parse_uint(s, value);
	return 1;
}

static void sleep_ms(uint32_t ms)
{
	struct timespec ts = { (time_t)(ms / 1000), (long)(ms % 1000) * 1000000L };
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
	char *argv[5] = { 0 };
	int argc = 0;
	for (char *tok = strtok(line, " \t\r\n"); tok && argc < 5; tok = strtok(NULL, " \t\r\n"))
	{
		argv[argc++] = tok;
	}
	if (argc == 0 || argv[0][0] == '#')
	{
		return 0;
	}
	const char *cmd = argv[0];
	double x, y;
	uint32_t value;
	if (strcmp(cmd, "move") == 0 && argc == 3 && parse_coord(argv[1], &x) && parse_coord(argv[2], &y) && need(3, cmd))
	{
		org_kde_kwin_fake_input_pointer_motion_absolute(fake, wl_fixed_from_double(x), wl_fixed_from_double(y));
	}
	else if (strcmp(cmd, "rel") == 0 && argc == 3 && parse_coord(argv[1], &x) && parse_coord(argv[2], &y))
	{
		org_kde_kwin_fake_input_pointer_motion(fake, wl_fixed_from_double(x), wl_fixed_from_double(y));
	}
	else if ((strcmp(cmd, "down") == 0 || strcmp(cmd, "up") == 0 || strcmp(cmd, "click") == 0) && argc == 2 && parse_button(argv[1], &value))
	{
		uint32_t button = value;
		if (strcmp(cmd, "up") != 0)
		{
			org_kde_kwin_fake_input_button(fake, button, WL_POINTER_BUTTON_STATE_PRESSED);
		}
		if (strcmp(cmd, "down") != 0)
		{
			org_kde_kwin_fake_input_button(fake, button, WL_POINTER_BUTTON_STATE_RELEASED);
		}
	}
	else if (strcmp(cmd, "wheel") == 0 && argc == 3 && (strcmp(argv[1], "h") == 0 || strcmp(argv[1], "v") == 0) && parse_coord(argv[2], &y))
	{
		uint32_t axis = argv[1][0] == 'h' ? WL_POINTER_AXIS_HORIZONTAL_SCROLL : WL_POINTER_AXIS_VERTICAL_SCROLL;
		org_kde_kwin_fake_input_axis(fake, axis, wl_fixed_from_double(y));
	}
	else if ((strcmp(cmd, "keydown") == 0 || strcmp(cmd, "keyup") == 0 || strcmp(cmd, "tap") == 0) && argc == 2 && parse_uint(argv[1], &value) && need(4, cmd))
	{
		uint32_t key = value;
		if (strcmp(cmd, "keyup") != 0)
		{
			org_kde_kwin_fake_input_keyboard_key(fake, key, WL_KEYBOARD_KEY_STATE_PRESSED);
		}
		if (strcmp(cmd, "keydown") != 0)
		{
			org_kde_kwin_fake_input_keyboard_key(fake, key, WL_KEYBOARD_KEY_STATE_RELEASED);
		}
	}
	else if ((strcmp(cmd, "symdown") == 0 || strcmp(cmd, "symup") == 0 || strcmp(cmd, "symtap") == 0) && argc == 2 && parse_uint(argv[1], &value) && need(6, cmd))
	{
		uint32_t sym = value;
		if (strcmp(cmd, "symup") != 0)
		{
			org_kde_kwin_fake_input_keyboard_keysym(fake, sym, WL_KEYBOARD_KEY_STATE_PRESSED);
		}
		if (strcmp(cmd, "symdown") != 0)
		{
			org_kde_kwin_fake_input_keyboard_keysym(fake, sym, WL_KEYBOARD_KEY_STATE_RELEASED);
		}
	}
	else if (strcmp(cmd, "touchdown") == 0 && argc == 4 && parse_uint(argv[1], &value) && parse_coord(argv[2], &x) && parse_coord(argv[3], &y) && need(2, cmd))
	{
		org_kde_kwin_fake_input_touch_down(fake, value, wl_fixed_from_double(x), wl_fixed_from_double(y));
	}
	else if (strcmp(cmd, "touchmove") == 0 && argc == 4 && parse_uint(argv[1], &value) && parse_coord(argv[2], &x) && parse_coord(argv[3], &y) && need(2, cmd))
	{
		org_kde_kwin_fake_input_touch_motion(fake, value, wl_fixed_from_double(x), wl_fixed_from_double(y));
	}
	else if (strcmp(cmd, "touchup") == 0 && argc == 2 && parse_uint(argv[1], &value) && need(2, cmd))
	{
		org_kde_kwin_fake_input_touch_up(fake, value);
	}
	else if (strcmp(cmd, "touchframe") == 0 && argc == 1 && need(2, cmd))
	{
		org_kde_kwin_fake_input_touch_frame(fake);
	}
	else if (strcmp(cmd, "sleep") == 0 && argc == 2 && parse_uint(argv[1], &value))
	{
		sleep_ms(value);
	}
	else
	{
		fprintf(stderr, "uno-wl-inject: bad command '%s'\n", cmd);
		return 1;
	}
	return 0;
}

static int roundtrip(struct wl_display *display)
{
	if (wl_display_roundtrip(display) >= 0) return 1;
	int error = wl_display_get_error(display);
	fprintf(stderr, "uno-wl-inject: display roundtrip failed: %s (%d)\n", strerror(error), error);
	return 0;
}

int main(int argc, char **argv)
{
	const char *socket = getenv("UNO_WL_INJECT_SOCKET");
	int allow_session = 0, first = 1, status = 0;
	struct wl_display *display = NULL;
	struct wl_registry *registry = NULL;
	while (first < argc)
	{
		if (strcmp(argv[first], "--socket") == 0 && first + 1 < argc)
		{
			socket = argv[first + 1];
			first += 2;
		}
		else if (strcmp(argv[first], "--allow-session") == 0)
		{
			allow_session = 1;
			first++;
		}
		else if (strcmp(argv[first], "--") == 0)
		{
			first++;
			break;
		}
		else if (strncmp(argv[first], "--", 2) == 0)
		{
			fprintf(stderr, "uno-wl-inject: invalid option or missing socket name\n");
			return 2;
		}
		else break;
	}
	const char *basename = socket ? strrchr(socket, '/') : NULL;
	basename = basename ? basename + 1 : socket;
	if (!socket || !*socket || !*basename || (!allow_session &&
		(strcmp(basename, "wayland-0") == 0 || strcmp(basename, "wayland-1") == 0)))
	{
		fprintf(stderr, "uno-wl-inject: require --socket NAME or UNO_WL_INJECT_SOCKET; wayland-0/wayland-1 require --allow-session\n");
		return 2;
	}
	// libwayland otherwise prefers an inherited WAYLAND_SOCKET fd over the explicit name.
	if (unsetenv("WAYLAND_SOCKET") != 0) return 2;
	display = wl_display_connect(socket);
	if (!display)
	{
		fprintf(stderr, "uno-wl-inject: cannot connect to socket '%s': %s\n", socket, strerror(errno));
		return 2;
	}
	registry = wl_display_get_registry(display);
	if (!registry || wl_registry_add_listener(registry, &registry_listener, NULL) < 0)
	{
		status = 4;
		goto cleanup;
	}
	if (!roundtrip(display)) { status = 4; goto cleanup; }
	if (!fake)
	{
		fprintf(stderr, "uno-wl-inject: org_kde_kwin_fake_input not advertised (private KWin needs KWIN_WAYLAND_NO_PERMISSION_CHECKS=1 or an X-KDE-Wayland-Interfaces grant)\n");
		status = 3;
		goto cleanup;
	}
	org_kde_kwin_fake_input_authenticate(fake, "uno-wl-inject", "Automated Uno Wayland input tests");
	if (!roundtrip(display)) { status = 4; goto cleanup; }

	char buffer[256];
	int i = first;
	for (;;)
	{
		size_t length = 0;
		int oversized = 0;
		if (first < argc)
		{
			if (i == argc) break;
			length = strlen(argv[i]);
			oversized = length >= sizeof buffer;
			if (!oversized) memcpy(buffer, argv[i], length + 1);
			i++;
		}
		else
		{
			int ch;
			// Drain the entire physical line even when it exceeds the command limit.
			while ((ch = fgetc(stdin)) != EOF && ch != '\n')
			{
				if (length < sizeof buffer - 1) buffer[length++] = (char)ch;
				else oversized = 1;
				if (ch == 0) oversized = 1;
			}
			if (ferror(stdin)) { perror("uno-wl-inject: stdin"); status = 1; break; }
			if (ch == EOF && length == 0 && !oversized) break;
			buffer[length] = 0;
		}
		if (oversized)
		{
			fprintf(stderr, "uno-wl-inject: oversized or invalid command line\n");
			status = 1;
			continue;
		}
		if (run(buffer)) status = 1;
		if (!roundtrip(display)) { status = 4; goto cleanup; }
	}
	if (fake_version >= 5)
	{
		org_kde_kwin_fake_input_destroy(fake);
		fake = NULL;
	}
	if (!roundtrip(display)) status = 4;
cleanup:
	if (fake) wl_proxy_destroy((struct wl_proxy *)fake);
	if (registry) wl_registry_destroy(registry);
	wl_display_disconnect(display);
	return status;
}
