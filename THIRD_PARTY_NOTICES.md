# Third-party notices

This document records development dependencies. AileArc's own code is licensed under the MIT License in LICENSE; the dependencies below retain their own terms.

## 7-Zip 26.03

- Author: Igor Pavlov and contributors.
- Source: https://github.com/ip7z/7zip/releases/tag/26.03
- Upstream source distribution: https://github.com/ip7z/7zip/releases/download/26.03/7z2603-src.7z
- License: LGPL-2.1-or-later for most code; 7z.dll also includes BSD-licensed portions and the unRAR restriction.
- Modification: native binaries are unmodified. The managed ABI adapter is local project code.
- Bootstrap downloads official artifacts with pinned SHA-256 checksums. Native binaries are not committed.
- The upstream License.txt is copied beside the Worker as 7zip-License.txt. A future binary release must retain the corresponding notices and meet the applicable distribution obligations.
- The unRAR code must not be used to recreate the proprietary RAR compression algorithm.

## Windows App SDK / WinUI

- Package: Microsoft.WindowsAppSDK 1.8.260804001; transitive versions are pinned in packages.lock.json.
- Source and license information: https://github.com/microsoft/WindowsAppSDK and https://github.com/microsoft/microsoft-ui-xaml
- NuGet packages retain their upstream licenses; do not treat all transitive binaries as having one project-wide license.

## Test tooling

- xUnit 2.9.3 and xunit.runner.visualstudio 3.1.4: Apache-2.0, https://github.com/xunit/xunit
- Microsoft.NET.Test.Sdk 17.14.1: MIT, https://github.com/microsoft/vstest

## libarchive RAR test fixture

The base64 fixture in ExtractionTests.cs is a representation of
https://github.com/libarchive/libarchive/blob/master/libarchive/test/test_read_format_rar.rar.uu
retrieved on 2026-09-15. It is used only for tests, not linked as an engine.

Upstream distribution copyright holder: Tim Kientzle and libarchive contributors.
The libarchive distribution notice is at https://github.com/libarchive/libarchive/blob/master/COPYING.

Copyright (c) 2003-2018 <author(s)>
All rights reserved.

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions
are met:
1. Redistributions of source code must retain the above copyright
   notice, this list of conditions and the following disclaimer
   in this position and unchanged.
2. Redistributions in binary form must reproduce the above copyright
   notice, this list of conditions and the following disclaimer in the
   documentation and/or other materials provided with the distribution.

THIS SOFTWARE IS PROVIDED BY THE AUTHOR(S) ``AS IS'' AND ANY EXPRESS OR
IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED WARRANTIES
OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE DISCLAIMED.
IN NO EVENT SHALL THE AUTHOR(S) BE LIABLE FOR ANY DIRECT, INDIRECT,
INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT
NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE,
DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY
THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
(INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF
THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
