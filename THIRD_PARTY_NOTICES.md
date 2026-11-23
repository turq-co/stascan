# Third-party notices

## Intel PresentMon (downloaded on request, not included)

FPS measurement uses [PresentMon](https://github.com/GameTechDev/PresentMon) 2.6.0 by Intel Corporation, licensed under the MIT License. StaScan does not ship it: when you ask for FPS measurement, it downloads `PresentMon-2.6.0-x64.exe` from the official GitHub release and verifies its SHA-256 checksum before running it.

## cpu-direct-usb (USB Latency Analyzer V2)

`src/UsbTopology.cs` ports the algorithm and the USB host controller table from
https://github.com/MariusHeier/cpu-direct-usb (the table there was compiled from Linux `xhci-pci.c` and `pci.ids`).

```
MIT License

Copyright (c) 2025 Marius Heier

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```
