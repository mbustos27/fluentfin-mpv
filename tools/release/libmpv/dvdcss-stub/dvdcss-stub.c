/*
 * FluentFin MPV: a stand-in for libdvdcss-2.dll (DVD CSS decryption), which FluentFin MPV does not ship.
 *
 * MSYS2's libdvdread-8.dll imports these seven functions from libdvdcss-2.dll, so the DLL must exist for libmpv to
 * load. Every function fails: no DVD device or image can be opened through it, so DVDs (a Jellyfin client never plays
 * one) are not supported. Nothing here comes from libdvdcss; the signatures follow its public API.
 *
 * Copyright (C) 2026 FluentFin MPV contributors
 *
 * This program is free software: you can redistribute it and/or modify it under the terms of the GNU General Public
 * License as published by the Free Software Foundation, either version 3 of the License, or (at your option) any later
 * version. This program is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the
 * implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the GNU General Public License for more
 * details. You should have received a copy of the GNU General Public License along with this program. If not, see
 * <https://www.gnu.org/licenses/>.
 */
#include <stdint.h>
#include <stddef.h>

typedef struct dvdcss_s *dvdcss_t;

/* Opening a device, an image or a stream always fails: libdvdread reports that it could not open the DVD. */
dvdcss_t dvdcss_open(const char *target) { (void)target; return NULL; }
dvdcss_t dvdcss_open_stream(void *stream, void *callbacks) { (void)stream; (void)callbacks; return NULL; }

/* Never reached with a handle of ours (none is ever returned); fail anyway. */
int dvdcss_close(dvdcss_t dvdcss) { (void)dvdcss; return -1; }
int dvdcss_seek(dvdcss_t dvdcss, int blocks, int flags) { (void)dvdcss; (void)blocks; (void)flags; return -1; }
int dvdcss_read(dvdcss_t dvdcss, void *buffer, int blocks, int flags) { (void)dvdcss; (void)buffer; (void)blocks; (void)flags; return -1; }
int dvdcpxm_init(dvdcss_t dvdcss, uint8_t *input) { (void)dvdcss; (void)input; return -1; }
int dvdcpxm_read(dvdcss_t dvdcss, void *buffer, int blocks, int flags) { (void)dvdcss; (void)buffer; (void)blocks; (void)flags; return -1; }
