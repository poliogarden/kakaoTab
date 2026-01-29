using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;

namespace KakaoTab
{
    /// <summary>
    /// ROI 해시 패키지 (dHash + 평균 밝기)
    /// </summary>
    public readonly struct RoiHashPack
    {
        public readonly ulong Hash;
        public readonly byte Mean;

        public RoiHashPack(ulong hash, byte mean)
        {
            Hash = hash;
            Mean = mean;
        }
    }

    /// <summary>
    /// ROI 종류
    /// </summary>
    public enum RoiKind
    {
        Bottom,    // 하단 메시지 영역 (가장 중요)
        Mid,       // 중간 메시지 영역 (보조)
        Aux        // 보조 영역 (좁게)
    }

    /// <summary>
    /// 픽셀 변화 감지를 통한 새 메시지 감지 유틸리티
    /// </summary>
    public static class PixelActivityDetector
    {
        private const bool DEBUG_PIXEL = true; // 디버그 모드

        // Bottom ROI (하단 메시지 영역)
        private const double BottomRoiWidthRatio = 0.45;
        private const double BottomRoiHeightRatio = 0.25;
        private const double BottomRoiXOffsetRatio = 0.52;
        private const double BottomRoiYOffsetRatio = 0.68;

        // Mid ROI (중간 메시지 영역)
        private const double MidRoiWidthRatio = 0.40;
        private const double MidRoiHeightRatio = 0.20;
        private const double MidRoiXOffsetRatio = 0.50;
        private const double MidRoiYOffsetRatio = 0.45;

        // Aux ROI (보조 영역, 좁게)
        private const double AuxRoiWidthRatio = 0.30;
        private const double AuxRoiHeightRatio = 0.15;
        private const double AuxRoiXOffsetRatio = 0.35;
        private const double AuxRoiYOffsetRatio = 0.60;

        private const int DHashWidth = 9;              // dHash 너비 (9x8 = 72bit, 하지만 64bit만 사용)
        private const int DHashHeight = 8;             // dHash 높이
        private const int BlackThreshold = 10;          // 검정 화면 판별 임계값 (평균 밝기)
        private const int TinyDist = 3;                // 렌더링 중 판별용 작은 거리

        [DllImport("user32.dll")]
        private static extern IntPtr GetWindowDC(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int nWidth, int nHeight);

        [DllImport("gdi32.dll")]
        private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr hObject);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        private static extern bool BitBlt(IntPtr hObject, int nXDest, int nYDest, int nWidth, int nHeight, IntPtr hObjectSource, int nXSrc, int nYSrc, int dwRop);

        [DllImport("user32.dll")]
        private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint nFlags);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
            public int Width => Right - Left;
            public int Height => Bottom - Top;
        }

        private const int SRCCOPY = 0x00CC0020;
        private const uint PW_CLIENTONLY = 0x00000001;

        /// <summary>
        /// 창의 하단 ROI 영역을 캡처하여 dHash를 계산합니다. (하위 호환성)
        /// </summary>
        public static bool TryGetBottomRoiHash(IntPtr hwnd, out ulong hash)
        {
            hash = 0;
            if (TryGetRoiHashPack(hwnd, RoiKind.Bottom, out RoiHashPack pack))
            {
                hash = pack.Hash;
                return true;
            }
            return false;
        }

        /// <summary>
        /// 지정된 ROI 종류에 대해 해시 패키지(dHash + 평균 밝기)를 계산합니다.
        /// </summary>
        public static bool TryGetRoiHashPack(IntPtr hwnd, RoiKind kind, out RoiHashPack pack)
        {
            pack = default;

            try
            {
                // ROI 영역 계산
                if (!GetRoiFromClient(hwnd, kind, out Rectangle roi))
                {
                    return false;
                }

                // 1차: BitBlt 방식으로 캡처 시도
                Bitmap? bitmap = null;
                if (TryCaptureRoi_BitBlt(hwnd, roi, out bitmap) && bitmap != null)
                {
                    // 검정 화면 체크
                    if (IsBlackScreen(bitmap))
                    {
                        bitmap.Dispose();
                        
                        // 2차 fallback: PrintWindow 시도
                        bitmap = null;
                        if (TryCaptureRoi_PrintWindow(hwnd, roi, out bitmap) && bitmap != null)
                        {
                            if (IsBlackScreen(bitmap))
                            {
                                bitmap.Dispose();
                                return false;
                            }
                            // PrintWindow 성공
                        }
                        else
                        {
                            return false;
                        }
                    }
                    // BitBlt 성공 (검정 아님)
                }
                else
                {
                    // 2차 fallback: PrintWindow 시도
                    bitmap = null;
                    if (!TryCaptureRoi_PrintWindow(hwnd, roi, out bitmap) || bitmap == null)
                    {
                        return false;
                    }
                    
                    // PrintWindow 결과도 검정 화면 체크
                    if (IsBlackScreen(bitmap))
                    {
                        bitmap.Dispose();
                        return false;
                    }
                }

                // dHash + 평균 밝기 계산
                if (bitmap != null)
                {
                    ulong hash = ComputeDHash64(bitmap);
                    byte mean = ComputeMeanLuminance(bitmap);
                    pack = new RoiHashPack(hash, mean);
                    bitmap.Dispose();
                    return true;
                }
                return false;
            }
            catch (Exception ex)
            {
                if (DEBUG_PIXEL)
                {
                    System.Diagnostics.Debug.WriteLine($"[Pixel] Exception in TryGetRoiHashPack hwnd={hwnd}, kind={kind}, error={ex.Message}");
                }
                return false;
            }
        }

        /// <summary>
        /// 클라이언트 영역에서 지정된 종류의 ROI 영역을 계산합니다.
        /// </summary>
        private static bool GetRoiFromClient(IntPtr hwnd, RoiKind kind, out Rectangle roi)
        {
            roi = Rectangle.Empty;

            if (!GetClientRect(hwnd, out RECT clientRect))
            {
                return false;
            }

            int clientWidth = clientRect.Width;
            int clientHeight = clientRect.Height;

            if (clientWidth <= 0 || clientHeight <= 0)
            {
                return false;
            }

            // ROI 종류에 따라 비율 선택
            double widthRatio, heightRatio, xOffsetRatio, yOffsetRatio;
            switch (kind)
            {
                case RoiKind.Bottom:
                    widthRatio = BottomRoiWidthRatio;
                    heightRatio = BottomRoiHeightRatio;
                    xOffsetRatio = BottomRoiXOffsetRatio;
                    yOffsetRatio = BottomRoiYOffsetRatio;
                    break;
                case RoiKind.Mid:
                    widthRatio = MidRoiWidthRatio;
                    heightRatio = MidRoiHeightRatio;
                    xOffsetRatio = MidRoiXOffsetRatio;
                    yOffsetRatio = MidRoiYOffsetRatio;
                    break;
                case RoiKind.Aux:
                    widthRatio = AuxRoiWidthRatio;
                    heightRatio = AuxRoiHeightRatio;
                    xOffsetRatio = AuxRoiXOffsetRatio;
                    yOffsetRatio = AuxRoiYOffsetRatio;
                    break;
                default:
                    return false;
            }

            // ROI 영역 계산 (비율 기반)
            int roiX = (int)(clientWidth * xOffsetRatio);
            int roiY = (int)(clientHeight * yOffsetRatio);
            int roiW = (int)(clientWidth * widthRatio);
            int roiH = (int)(clientHeight * heightRatio);

            // ROI가 클라이언트 영역을 벗어나지 않도록 clamp 처리
            if (roiX < 0) roiX = 0;
            if (roiY < 0) roiY = 0;
            if (roiX + roiW > clientWidth) roiW = clientWidth - roiX;
            if (roiY + roiH > clientHeight) roiH = clientHeight - roiY;

            if (roiW <= 0 || roiH <= 0)
            {
                return false;
            }

            roi = new Rectangle(roiX, roiY, roiW, roiH);
            return true;
        }

        /// <summary>
        /// BitBlt 방식으로 ROI 캡처
        /// </summary>
        private static bool TryCaptureRoi_BitBlt(IntPtr hwnd, Rectangle roi, out Bitmap? bitmap)
        {
            bitmap = null;
            IntPtr hdcSrc = IntPtr.Zero;
            IntPtr hdcDest = IntPtr.Zero;
            IntPtr hBitmap = IntPtr.Zero;
            IntPtr hOldBitmap = IntPtr.Zero;

            try
            {
                hdcSrc = GetWindowDC(hwnd);
                if (hdcSrc == IntPtr.Zero)
                    return false;

                hdcDest = CreateCompatibleDC(hdcSrc);
                if (hdcDest == IntPtr.Zero)
                    return false;

                hBitmap = CreateCompatibleBitmap(hdcSrc, roi.Width, roi.Height);
                if (hBitmap == IntPtr.Zero)
                    return false;

                hOldBitmap = SelectObject(hdcDest, hBitmap);

                // ROI 영역을 BitBlt로 복사
                bool success = BitBlt(hdcDest, 0, 0, roi.Width, roi.Height, hdcSrc, roi.X, roi.Y, SRCCOPY);
                if (!success)
                    return false;

                SelectObject(hdcDest, hOldBitmap);
                hOldBitmap = IntPtr.Zero;

                // Bitmap 객체 생성
                bitmap = Image.FromHbitmap(hBitmap);
                return true;
            }
            catch
            {
                if (bitmap != null)
                {
                    bitmap.Dispose();
                    bitmap = null;
                }
                return false;
            }
            finally
            {
                if (hOldBitmap != IntPtr.Zero)
                    SelectObject(hdcDest, hOldBitmap);
                if (hBitmap != IntPtr.Zero)
                    DeleteObject(hBitmap);
                if (hdcDest != IntPtr.Zero)
                    DeleteDC(hdcDest);
                if (hdcSrc != IntPtr.Zero)
                    ReleaseDC(hwnd, hdcSrc);
            }
        }

        /// <summary>
        /// PrintWindow 방식으로 ROI 캡처
        /// </summary>
        private static bool TryCaptureRoi_PrintWindow(IntPtr hwnd, Rectangle roi, out Bitmap? bitmap)
        {
            bitmap = null;
            IntPtr hdcSrc = IntPtr.Zero;
            IntPtr hdcDest = IntPtr.Zero;
            IntPtr hBitmap = IntPtr.Zero;
            IntPtr hOldBitmap = IntPtr.Zero;

            try
            {
                if (!GetClientRect(hwnd, out RECT clientRect))
                    return false;

                hdcSrc = GetWindowDC(IntPtr.Zero); // Desktop DC
                if (hdcSrc == IntPtr.Zero)
                    return false;

                hdcDest = CreateCompatibleDC(hdcSrc);
                if (hdcDest == IntPtr.Zero)
                    return false;

                hBitmap = CreateCompatibleBitmap(hdcSrc, clientRect.Width, clientRect.Height);
                if (hBitmap == IntPtr.Zero)
                    return false;

                hOldBitmap = SelectObject(hdcDest, hBitmap);

                // PrintWindow로 전체 클라이언트 영역 캡처
                bool success = PrintWindow(hwnd, hdcDest, PW_CLIENTONLY);
                if (!success)
                    return false;

                SelectObject(hdcDest, hOldBitmap);
                hOldBitmap = IntPtr.Zero;

                // 전체 Bitmap 생성 후 ROI만 잘라내기
                Bitmap fullBitmap = Image.FromHbitmap(hBitmap);
                try
                {
                    if (roi.X + roi.Width > fullBitmap.Width || roi.Y + roi.Height > fullBitmap.Height)
                    {
                        fullBitmap.Dispose();
                        return false;
                    }

                    Rectangle roiRect = new Rectangle(roi.X, roi.Y, roi.Width, roi.Height);
                    bitmap = fullBitmap.Clone(roiRect, PixelFormat.Format24bppRgb);
                }
                finally
                {
                    fullBitmap.Dispose();
                }

                return bitmap != null;
            }
            catch
            {
                if (bitmap != null)
                {
                    bitmap.Dispose();
                    bitmap = null;
                }
                return false;
            }
            finally
            {
                if (hOldBitmap != IntPtr.Zero)
                    SelectObject(hdcDest, hOldBitmap);
                if (hBitmap != IntPtr.Zero)
                    DeleteObject(hBitmap);
                if (hdcDest != IntPtr.Zero)
                    DeleteDC(hdcDest);
                if (hdcSrc != IntPtr.Zero)
                    ReleaseDC(IntPtr.Zero, hdcSrc);
            }
        }

        /// <summary>
        /// 검정 화면인지 판별 (평균 밝기 체크)
        /// </summary>
        private static bool IsBlackScreen(Bitmap? bitmap)
        {
            if (bitmap == null || bitmap.Width == 0 || bitmap.Height == 0)
                return true;

            try
            {
                // 샘플 5개 픽셀의 평균 밝기 계산
                int sampleCount = 5;
                int totalBrightness = 0;
                int width = bitmap.Width;
                int height = bitmap.Height;

                // 중앙 + 4개 모서리 근처 샘플링
                int[] sampleX = { width / 2, width / 4, width * 3 / 4, width / 4, width * 3 / 4 };
                int[] sampleY = { height / 2, height / 4, height / 4, height * 3 / 4, height * 3 / 4 };

                for (int i = 0; i < sampleCount && i < sampleX.Length; i++)
                {
                    int x = Math.Min(sampleX[i], width - 1);
                    int y = Math.Min(sampleY[i], height - 1);
                    Color pixel = bitmap.GetPixel(x, y);
                    int brightness = (int)(pixel.R * 0.299 + pixel.G * 0.587 + pixel.B * 0.114);
                    totalBrightness += brightness;
                }

                int avgBrightness = totalBrightness / sampleCount;
                return avgBrightness <= BlackThreshold;
            }
            catch
            {
                return true; // 에러 시 검정으로 간주
            }
        }

        /// <summary>
        /// 64-bit dHash 계산 (9x8 그레이스케일 -> 64bit)
        /// </summary>
        private static ulong ComputeDHash64(Bitmap bitmap)
        {
            // 9x8 그레이스케일로 축소
            using (Bitmap resized = new Bitmap(bitmap, DHashWidth, DHashHeight))
            {
                ulong hash = 0;
                int bitIndex = 0;

                // 각 행에서 인접 픽셀 밝기 비교 (9개 픽셀 -> 8개 비교)
                for (int y = 0; y < DHashHeight; y++)
                {
                    for (int x = 0; x < DHashWidth - 1; x++)
                    {
                        Color pixel1 = resized.GetPixel(x, y);
                        Color pixel2 = resized.GetPixel(x + 1, y);

                        // 그레이스케일 밝기 계산
                        int brightness1 = (int)(pixel1.R * 0.299 + pixel1.G * 0.587 + pixel1.B * 0.114);
                        int brightness2 = (int)(pixel2.R * 0.299 + pixel2.G * 0.587 + pixel2.B * 0.114);

                        // 인접 픽셀보다 밝으면 1, 아니면 0
                        if (brightness1 > brightness2)
                        {
                            hash |= (1UL << bitIndex);
                        }

                        bitIndex++;
                        if (bitIndex >= 64) break; // 64bit만 사용
                    }
                    if (bitIndex >= 64) break;
                }

                return hash;
            }
        }

        /// <summary>
        /// 두 해시 간의 해밍 거리 계산
        /// </summary>
        public static int HammingDistance(ulong a, ulong b)
        {
            ulong xor = a ^ b;
            
            // BitOperations.PopCount 사용 (.NET 5+)
            return BitOperations.PopCount(xor);
        }

        /// <summary>
        /// 평균 밝기(Mean Luminance) 계산
        /// </summary>
        private static byte ComputeMeanLuminance(Bitmap bitmap)
        {
            if (bitmap == null || bitmap.Width == 0 || bitmap.Height == 0)
                return 0;

            try
            {
                long totalBrightness = 0;
                int pixelCount = 0;

                // 전체 픽셀의 평균 밝기 계산 (샘플링으로 최적화 가능하지만 정확도를 위해 전체 계산)
                for (int y = 0; y < bitmap.Height; y++)
                {
                    for (int x = 0; x < bitmap.Width; x++)
                    {
                        Color pixel = bitmap.GetPixel(x, y);
                        int brightness = (int)(pixel.R * 0.299 + pixel.G * 0.587 + pixel.B * 0.114);
                        totalBrightness += brightness;
                        pixelCount++;
                    }
                }

                if (pixelCount == 0)
                    return 0;

                int mean = (int)(totalBrightness / pixelCount);
                return (byte)Math.Min(255, Math.Max(0, mean));
            }
            catch
            {
                return 0;
            }
        }
    }
}
