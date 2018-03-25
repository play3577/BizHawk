﻿using System;

using BizHawk.Common.BufferExtensions;
using BizHawk.Emulation.Common;
using BizHawk.Emulation.Common.Components.LR35902;
using BizHawk.Common.NumberExtensions;

using BizHawk.Emulation.Cores.Consoles.Nintendo.Gameboy;
using System.Runtime.InteropServices;

namespace BizHawk.Emulation.Cores.Nintendo.GBHawk
{
	[Core(
		"GBHawk",
		"",
		isPorted: false,
		isReleased: false)]
	[ServiceNotApplicable(typeof(IDriveLight))]
	public partial class GBHawk : IEmulator, ISaveRam, IDebuggable, IStatable, IInputPollable, IRegionable, IGameboyCommon,
	ISettable<GBHawk.GBSettings, GBHawk.GBSyncSettings>
	{
		// this register controls whether or not the GB BIOS is mapped into memory
		public byte GB_bios_register;

		public byte input_register;

		// The unused bits in this register are still read/writable
		public byte REG_FFFF;
		// The unused bits in this register (interrupt flags) are always set
		public byte REG_FF0F = 0xE0;
		public bool enable_VBL;
		public bool enable_STAT;
		public bool enable_TIMO;
		public bool enable_SER;
		public bool enable_PRS;


		// memory domains
		public byte[] RAM = new byte[0x8000]; // only 0x2000 available to GB
		public byte[] ZP_RAM = new byte[0x80];
		/* 
		 * VRAM is arranged as: 
		 * 0x1800 Tiles
		 * 0x400 BG Map 1
		 * 0x400 BG Map 2
		 * 0x1800 Tiles
		 * 0x400 CA Map 1
		 * 0x400 CA Map 2
		 * Only the top set is available in GB (i.e. VRAM_Bank = 0)
		 */
		public byte[] VRAM = new byte[0x4000];
		public byte[] OAM = new byte[0xA0];

		public int RAM_Bank;
		public byte VRAM_Bank;
		public bool is_GBC;
		public bool double_speed;
		public bool speed_switch;
		public bool HDMA_transfer; // stalls CPU when in progress

		public byte[] _bios;
		public readonly byte[] _rom;		
		public readonly byte[] header = new byte[0x50];

		public byte[] cart_RAM;
		public bool has_bat;

		private int _frame = 0;

		public bool Use_RTC;

		public MapperBase mapper;

		private readonly ITraceable _tracer;

		public LR35902 cpu;
		public PPU ppu;
		public Timer timer;
		public Audio audio;
		public SerialPort serialport;

		private static byte[] GBA_override = { 0xFF, 0x00, 0xCD, 0x03, 0x35, 0xAA, 0x31, 0x90, 0x94, 0x00, 0x00, 0x00, 0x00 };

		[CoreConstructor("GB", "GBC")]
		public GBHawk(CoreComm comm, GameInfo game, byte[] rom, /*string gameDbFn,*/ object settings, object syncSettings)
		{
			var ser = new BasicServiceProvider(this);

			cpu = new LR35902
			{
				ReadMemory = ReadMemory,
				WriteMemory = WriteMemory,
				PeekMemory = PeekMemory,
				DummyReadMemory = ReadMemory,
				OnExecFetch = ExecFetch,
				SpeedFunc = SpeedFunc,
			};
			
			timer = new Timer();
			audio = new Audio();
			serialport = new SerialPort();

			CoreComm = comm;

			_settings = (GBSettings)settings ?? new GBSettings();
			_syncSettings = (GBSyncSettings)syncSettings ?? new GBSyncSettings();
			_controllerDeck = new GBHawkControllerDeck(_syncSettings.Port1);

			byte[] Bios = null;

			// Load up a BIOS and initialize the correct PPU
			if (_syncSettings.ConsoleMode == GBSyncSettings.ConsoleModeType.Auto)
			{
				if (game.System == "GB")
				{
					Bios = comm.CoreFileProvider.GetFirmware("GB", "World", true, "BIOS Not Found, Cannot Load");
					ppu = new GB_PPU();
				}
				else
				{
					Bios = comm.CoreFileProvider.GetFirmware("GBC", "World", true, "BIOS Not Found, Cannot Load");
					ppu = new GBC_PPU();
					is_GBC = true;
				}
				
			}
			else if (_syncSettings.ConsoleMode == GBSyncSettings.ConsoleModeType.GB)
			{
				Bios = comm.CoreFileProvider.GetFirmware("GB", "World", true, "BIOS Not Found, Cannot Load");
				ppu = new GB_PPU();
			}
			else
			{
				Bios = comm.CoreFileProvider.GetFirmware("GBC", "World", true, "BIOS Not Found, Cannot Load");
				ppu = new GBC_PPU();
				is_GBC = true;
			}			

			if (Bios == null)
			{
				throw new MissingFirmwareException("Missing Gamboy Bios");
			}

			_bios = Bios;

			// Here we modify the BIOS if GBA mode is set (credit to ExtraTricky)
			if (is_GBC && _syncSettings.GBACGB)
			{
				for (int i = 0; i < 13; i++)
				{
					_bios[i + 0xF3] = (byte)((GBA_override[i] + _bios[i + 0xF3]) & 0xFF);
				}
			}

			// CPU needs to know about GBC status too
			cpu.is_GBC = is_GBC;

			Buffer.BlockCopy(rom, 0x100, header, 0, 0x50);

			string hash_md5 = null;
			hash_md5 = "md5:" + rom.HashMD5(0, rom.Length);
			Console.WriteLine(hash_md5);

			_rom = rom;
			Setup_Mapper();

			_frameHz = 60;

			timer.Core = this;
			audio.Core = this;
			ppu.Core = this;
			serialport.Core = this;

			ser.Register<IVideoProvider>(this);
			ser.Register<ISoundProvider>(audio);
			ServiceProvider = ser;

			_settings = (GBSettings)settings ?? new GBSettings();
			_syncSettings = (GBSyncSettings)syncSettings ?? new GBSyncSettings();

			_tracer = new TraceBuffer { Header = cpu.TraceHeader };
			ser.Register<ITraceable>(_tracer);

			SetupMemoryDomains();
			HardReset();

			iptr0 = Marshal.AllocHGlobal(VRAM.Length + 1);
			iptr1 = Marshal.AllocHGlobal(OAM.Length + 1);
			iptr2 = Marshal.AllocHGlobal(color_palette.Length * 2 * 8 + 1);
			iptr3 = Marshal.AllocHGlobal(color_palette.Length * 8 + 1);

			_scanlineCallback = null;
		}

		#region GPUViewer

		public bool IsCGBMode() => false;

		public IntPtr iptr0 = IntPtr.Zero;
		public IntPtr iptr1 = IntPtr.Zero;
		public IntPtr iptr2 = IntPtr.Zero;
		public IntPtr iptr3 = IntPtr.Zero;

		private GPUMemoryAreas _gpuMemory
		{
			get
			{
				Marshal.Copy(VRAM, 0, iptr0, VRAM.Length);
				Marshal.Copy(OAM, 0, iptr1, OAM.Length);

				int[] cp2 = new int[8];
				for (int i = 0; i < 4; i++)
				{
					cp2[i] = (int)color_palette[(ppu.obj_pal_0 >> (i * 2)) & 3];
					cp2[i + 4] = (int)color_palette[(ppu.obj_pal_1 >> (i * 2)) & 3];
				}
				Marshal.Copy(cp2, 0, iptr2, cp2.Length);

				int[] cp = new int[4];
				for (int i = 0; i < 4; i++)
				{
					cp[i] = (int)color_palette[(ppu.BGP >> (i * 2)) & 3];
				}
				Marshal.Copy(cp, 0, iptr3, cp.Length);


				return new GPUMemoryAreas(iptr0, iptr1, iptr2, iptr3);
			}
		} 

		public GPUMemoryAreas GetGPU() => _gpuMemory;

		public ScanlineCallback _scanlineCallback;
		public int _scanlineCallbackLine = 0;

		public void SetScanlineCallback(ScanlineCallback callback, int line)
		{
			_scanlineCallback = callback;
			_scanlineCallbackLine = line;

			if (line == -2)
			{
				GetGPU();
				_scanlineCallback(ppu.LCDC);
			}
		}

		private PrinterCallback _printerCallback = null;

		public void SetPrinterCallback(PrinterCallback callback)
		{
			_printerCallback = null;
		}

		#endregion

		public DisplayType Region => DisplayType.NTSC;

		private readonly GBHawkControllerDeck _controllerDeck;

		private void HardReset()
		{
			GB_bios_register = 0; // bios enable
			in_vblank = true; // we start off in vblank since the LCD is off
			in_vblank_old = true;

			RAM_Bank = 1; // RAM bank always starts as 1 (even writing zero still sets 1)

			Register_Reset();
			timer.Reset();
			ppu.Reset();
			audio.Reset();
			serialport.Reset();

			cpu.SetCallbacks(ReadMemory, PeekMemory, PeekMemory, WriteMemory);

			_vidbuffer = new int[VirtualWidth * VirtualHeight];
		}

		private void ExecFetch(ushort addr)
		{
			MemoryCallbacks.CallExecutes(addr, "System Bus");
		}

		private void Setup_Mapper()
		{
			// setup up mapper based on header entry
			string mppr;

			switch (header[0x47])
			{
				case 0x0: mapper = new MapperDefault();		mppr = "NROM";							break;
				case 0x1: mapper = new MapperMBC1();		mppr = "MBC1";							break;
				case 0x2: mapper = new MapperMBC1();		mppr = "MBC1";							break;
				case 0x3: mapper = new MapperMBC1();		mppr = "MBC1";		has_bat = true;		break;
				case 0x5: mapper = new MapperMBC2();		mppr = "MBC2";							break;
				case 0x6: mapper = new MapperMBC2();		mppr = "MBC2";		has_bat = true;		break;
				case 0x8: mapper = new MapperDefault();		mppr = "NROM";							break;
				case 0x9: mapper = new MapperDefault();		mppr = "NROM";		has_bat = true;		break;
				case 0xB: mapper = new MapperMMM01();		mppr = "MMM01";							break;
				case 0xC: mapper = new MapperMMM01();		mppr = "MMM01";							break;
				case 0xD: mapper = new MapperMMM01();		mppr = "MMM01";		has_bat = true;		break;
				case 0xF: mapper = new MapperMBC3();		mppr = "MBC3";		has_bat = true;		break;
				case 0x10: mapper = new MapperMBC3();		mppr = "MBC3";		has_bat = true;		break;
				case 0x11: mapper = new MapperMBC3();		mppr = "MBC3";							break;
				case 0x12: mapper = new MapperMBC3();		mppr = "MBC3";							break;
				case 0x13: mapper = new MapperMBC3();		mppr = "MBC3";		has_bat = true;		break;
				case 0x19: mapper = new MapperMBC5();		mppr = "MBC5";							break;
				case 0x1A: mapper = new MapperMBC5();		mppr = "MBC5";		has_bat = true;		break;
				case 0x1B: mapper = new MapperMBC5();		mppr = "MBC5";							break;
				case 0x1C: mapper = new MapperMBC5();		mppr = "MBC5";							break;
				case 0x1D: mapper = new MapperMBC5();		mppr = "MBC5";							break;
				case 0x1E: mapper = new MapperMBC5();		mppr = "MBC5";		has_bat = true;		break;
				case 0x20: mapper = new MapperMBC6();		mppr = "MBC6";							break;
				case 0x22: mapper = new MapperMBC7();		mppr = "MBC7";		has_bat = true;		break;
				case 0xFC: mapper = new MapperCamera();		mppr = "CAM";							break;
				case 0xFD: mapper = new MapperTAMA5();		mppr = "TAMA5";							break;
				case 0xFE: mapper = new MapperHuC3();		mppr = "HuC3";							break;
				case 0xFF: mapper = new MapperHuC1();		mppr = "HuC1";							break;

				// Bootleg mappers
				// NOTE: Sachen mapper selection does not account for scrambling, so if another bootleg mapper
				// identifies itself as 0x31, this will need to be modified
				case 0x31: mapper = new MapperSachen2();	mppr = "Schn2";							break;

				case 0x4:
				case 0x7:
				case 0xA:
				case 0xE:
				case 0x14:
				case 0x15:
				case 0x16:
				case 0x17:
				case 0x18:
				case 0x1F:
				case 0x21:
				default:
					// mapper not implemented
					Console.WriteLine(header[0x47]);
					throw new Exception("Mapper not implemented");
			}

			// special case for multi cart mappers
			if ((_rom.HashMD5(0,_rom.Length) == "97122B9B183AAB4079C8D36A4CE6E9C1") ||
				(_rom.HashMD5(0, _rom.Length) == "9FB9C42CF52DCFDCFBAD5E61AE1B5777") ||
				(_rom.HashMD5(0, _rom.Length) == "CF1F58AB72112716D3C615A553B2F481")				
				)
			{
				Console.WriteLine("Using Multi-Cart Mapper");
				mapper = new MapperMBC1Multi();
			}

			Console.Write("Mapper: ");
			Console.WriteLine(mppr);

			cart_RAM = null;

			switch (header[0x49])
			{
				case 1:
					cart_RAM = new byte[0x800];
					break;
				case 2:
					cart_RAM = new byte[0x2000];
					break;
				case 3:
					cart_RAM = new byte[0x8000];
					break;
				case 4:
					cart_RAM = new byte[0x20000];
					break;
				case 5:
					cart_RAM = new byte[0x10000];
					break;
				case 0:
					Console.WriteLine("Mapper Number indicates Battery Backed RAM but none present.");
					Console.WriteLine("Disabling Battery Setting.");
					has_bat = false;
					break;
			}

			// Sachen maper not known to have RAM
			if ((mppr == "Schn1") || (mppr == "Schn2"))
			{
				cart_RAM = null;
			}

			// mbc2 carts have built in RAM
			if (mppr == "MBC2")
			{
				cart_RAM = new byte[0x200];
			}

			mapper.Core = this;
			mapper.Initialize();

			if (cart_RAM != null)
			{

				Console.Write("RAM: "); Console.WriteLine(cart_RAM.Length);

				for (int i = 0; i < cart_RAM.Length; i++)
				{
					cart_RAM[i] = 0xFF;
				}
			}
			

			// Extra RTC initialization for mbc3
			if (mppr == "MBC3")
			{
				Use_RTC = true;
				int days = (int)Math.Floor(_syncSettings.RTCInitialTime / 86400.0);

				int days_upper = ((days & 0x100) >> 8) | ((days & 0x200) >> 2);

				mapper.RTC_Get((byte)days_upper, 4);
				mapper.RTC_Get((byte)(days & 0xFF), 3);

				int remaining = _syncSettings.RTCInitialTime - (days * 86400);

				int hours = (int)Math.Floor(remaining / 3600.0);

				mapper.RTC_Get((byte)(hours & 0xFF), 2);

				remaining = remaining - (hours * 3600);

				int minutes = (int)Math.Floor(remaining / 60.0);

				mapper.RTC_Get((byte)(minutes & 0xFF), 1);

				remaining = remaining - (minutes * 60);

				mapper.RTC_Get((byte)(remaining & 0xFF), 0);
			}
		}
	}
}
