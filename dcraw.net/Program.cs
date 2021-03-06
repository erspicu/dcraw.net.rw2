﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.IO;
using System.Drawing;
using System.Drawing.Imaging;
using System.Threading.Tasks;

using BitmapProcessing;

using System.Diagnostics;


//由 dcraw 移植簡化版 http://www.cybercom.net/~dcoffin/dcraw (原始為 C sources).
//以 panasonic rw2檔解碼為範例
//



namespace dcraw.net.rw2
{
    unsafe class Program
    {
        static FileStream ifp = null;
        static string ifname = "";
        static ushort raw_height, raw_width, height, width;
        static short order;
        static UInt32 tile_width, tile_length, load_flags;
        static UInt32 tiff_nifds, tiff_samples, tiff_bps, tiff_compress;
        static UInt32 thumb_offset, data_offset;
        static byte[] make = new byte[64];
        static byte[] model = new byte[64];
        static UInt32 filters;
        static float[] cam_mul = new float[4];
        static float[] pre_mul = new float[4];
        static int tiff_flip;
        static UInt32 thumb_length = 0;
        static ushort[,] raw_image;
        //static int colors = 3;
        static float[] scale_mul;

        static UInt32 maximum;

        static int height_m = 0; //, width_m = 0;

        struct rgb
        {
            public UInt16 r;
            public UInt16 g;
            public UInt16 b;
        }

        static rgb[,] demosaicing_image;

        static void Main(string[] args)
        {
            if (args.Length == 0)
                return;

            try
            {
                ifname = args[0];
                ifp = File.OpenRead(ifname);
            }
            catch
            {
                Console.WriteLine("參數或是檔案錯誤!\n");
                return;
            }



            Console.WriteLine("\nRW2檔Paser\n\n[檔案資訊]");
            identify();
            /* panasonic的rw2檔為tiff封裝型態(參考 http://lclevy.free.fr/raw ) 
             * ,使用tiff paser規則來擷取檔案相關重要處理資訊.這部分的bitwise處理
             * 很多我並不是非常了解,需要詳細閱讀檔案spec,以及熟悉bitwise的運算操作,
             * 但這部分偏於檔案io讀取解譯過程,並非是影像處理還原的真正階段.
             * 每一種格式的raw檔處理解譯過程都有許多差異,
             * 也是一般大眾不論是資工科班或非科班想處理raw第一個先卡死的關卡.
             * 而我個人也只是把C code重用C#寫,並沒有對詳細spec有太多琢磨.
             */
            Console.WriteLine("\nraw_width : {0} , width : {1}", raw_width, width);
            Console.WriteLine("raw_height : {0} , height : {1}", raw_height, height);
            Console.WriteLine("data_offset : {0}", data_offset);
            Console.WriteLine("WB param : {0} {1} {2} {3}", cam_mul[0], cam_mul[1], cam_mul[2], cam_mul[3]);
            Console.WriteLine("Bayer type : {0}\n", filters);


            //建立放置raw檔影像的二維陣列
            raw_image = new ushort[raw_height, raw_width];
            //將檔案seek到raw影像檔內容的起始位置
            ifp.Seek(data_offset, SeekOrigin.Begin);
            //開始解析和載入raw內容
            panasonic_load_raw();

            ifp.Close();

            if (args.Contains("-csv"))
                RawImageExport_CSV();
            //將載入的raw影像資料匯出成CSV標準格式,方便其他軟體讀取處理
            //如果你想靠其他軟體嘗試處理raw檔資料的話


            //要得到最基本的能夠觀看的影像還要將RAW影像每個位置的pixel利用鄰近pixel的r或g或b來內插填補成完整的RGB piexl.稱demosaicing.
            //如果要求得更精緻影像則還需要處理亮度增益.白平衡.去躁.gamma調整.動態範圍壓縮調整.變形校正.彩度.對比.色調調整等等後續步驟
            //詳細可參考 http://en.wikipedia.org/wiki/Raw_image_format "Processing" 一章節介紹

            height_m = (raw_height - height) / 2;
            demosaicing_image = new rgb[width, height];

            ImageWhitebalanceCameraSetup();

            raw_demosaicing();

            ImageToBitMap();//把自訂的影像結構轉化成C#通用的Bitmap物件

            if (args.Contains("-autowb"))
                ImageWhitebalanceAuto_grayworld(); //知名的白平衡處裡演算法,效果普普


            if (args.Contains("-hiseq"))
                HistogramEqualization(); //亮度延伸正規化,也具有白平衡修正效果

            int rwidth = 1024, rheight = 768;

            if (args.Contains("-orgsize"))
            {
                rwidth = width;
                rheight = height;
            }


            Bitmap reszie_img = ResizeLanczos(rwidth, rheight);
            // ResizeLanczos的程式碼佔掉很多行,不過其實不是解raw檔一定需要的東西
            // Lanczos號稱地表最強縮圖演算法,有沒有那麼好不知道,不過多數狀況下,它出來的結果的確讓人滿意


            reszie_img.Save(ifname + ".png", ImageFormat.Png);

            Console.WriteLine("\n全部過程完成!");
        }


        #region 後續處理




        //----------------------------- 傳說中的 Lanczos3 縮圖演算法 ,待速度優化 start
        // 參考 http://blog.csdn.net/yangzl2008/article/details/6693678 原Java Code
        static int nDots;
        static int nHalfDots;
        const double PI = 3.14159265358978;
        const double support = 3.0;
        static double[] contrib;
        static double[] normContrib;
        static double[] tmpContrib;
        static int scaleWidth;

        static Bitmap ResizeLanczos(int rwidth, int rheight)
        {

            Console.WriteLine("\nresize image...");



            int w = rwidth, h = rheight;

            double sx = w / (double)width;
            double sy = h / (double)height;

            if (sx > sy)
            {
                sx = sy;
                w = (int)(sx * width);
            }
            else
            {
                sy = sx;
                h = (int)(sy * height);
            }

            scaleWidth = w;

            if (determineResultSize(w, h) == 1)
            {
                Console.WriteLine("resize image done.");
                return rgbImage;
            }


            calContrib();
            Bitmap pbFinalOut = VerticalFiltering(HorizontalFiltering(rgbImage, w), h);

            Console.WriteLine("resize image done.");
            return pbFinalOut;
        }


        static double Lanczos(int i, int inWidth, int outWidth, double Support)
        {
            double x;
            x = (double)i * (double)outWidth / (double)inWidth;
            return Math.Sin(x * PI) / (x * PI) * Math.Sin(x * PI / Support) / (x * PI / Support);
        }

        static void calContrib()
        {
            nHalfDots = (int)((double)width * support / (double)scaleWidth);
            nDots = nHalfDots * 2 + 1;

            contrib = new double[nDots];
            normContrib = new double[nDots];
            tmpContrib = new double[nDots];

            int center = nHalfDots;
            contrib[center] = 1.0;

            double weight = 0.0;
            int i = 0;

            for (i = 1; i <= center; i++)
            {
                contrib[center + i] = Lanczos(i, width, scaleWidth, support);
                weight += contrib[center + i];
            }

            for (i = center - 1; i >= 0; i--)
                contrib[i] = contrib[center * 2 - i];

            weight = weight * 2 + (float)1.0;

            for (i = 0; i <= center; i++)
                normContrib[i] = contrib[i] / weight;

            for (i = center + 1; i < nDots; i++)
                normContrib[i] = normContrib[center * 2 - i];

        }

        static void CalTempContrib(int start, int stop)
        {
            double weight = 0;

            int i = 0;
            for (i = start; i <= stop; i++)
                weight += contrib[i];

            for (i = start; i <= stop; i++)
                tmpContrib[i] = contrib[i] / weight;

        }

        static int determineResultSize(int w, int h)
        {
            double scaleH, scaleV;
            scaleH = (double)w / (double)width;
            scaleV = (double)h / (double)height;
            if (scaleH >= 1.0 && scaleV >= 1.0)
                return 1;
            return 0;
        }



        static Bitmap VerticalFiltering(Bitmap pbImage, int iOutH)
        {
            int iW = pbImage.Width;
            int iH = pbImage.Height;


            double valueRed = 0.0;
            double valueGreen = 0.0;
            double valueBlue = 0.0;

            Bitmap pbOut = new Bitmap(iW, iOutH, PixelFormat.Format24bppRgb);

            FastBitmap processor = new FastBitmap(pbImage);
            processor.LockImage();

            FastBitmap processor_out = new FastBitmap(pbOut);
            processor_out.LockImage();

            byte* index;
            double tmp = 0;
            FastBitmap.PixelData* data;

            for (int y = 0; y < iOutH; y++)
            {

                int startY;
                int start;
                int Y = (int)(((double)y) * ((double)iH) / ((double)iOutH) + 0.5);

                startY = Y - nHalfDots;
                if (startY < 0)
                {
                    startY = 0;
                    start = nHalfDots - Y;
                }
                else
                {
                    start = 0;
                }

                int stop;
                int stopY = Y + nHalfDots;
                if (stopY > (int)(iH - 1))
                {
                    stopY = iH - 1;
                    stop = nHalfDots + (iH - 1 - Y);
                }
                else
                {
                    stop = nHalfDots * 2;
                }

                if (start > 0 || stop < nDots - 1)
                {

                    CalTempContrib(start, stop);
                    for (int x = 0; x < iW; x++)
                    {

                        valueBlue = valueGreen = valueRed = 0;
                        for (int i = startY, j = start; i <= stopY; i++, j++)
                        {
                            index = (processor.pBase + i * processor.width + x * pdsize);
                            tmp = tmpContrib[j];
                            valueRed += (((FastBitmap.PixelData*)(index)))->red * tmp;
                            valueGreen += (((FastBitmap.PixelData*)(index)))->green * tmp;
                            valueBlue += (((FastBitmap.PixelData*)(index)))->blue * tmp;


                        }

                        if (valueRed > 255) valueRed = 255;
                        else if (valueRed < 0) valueRed = 0;

                        if (valueGreen > 255) valueGreen = 255;
                        else if (valueGreen < 0) valueGreen = 0;

                        if (valueBlue > 255) valueBlue = 255;
                        else if (valueBlue < 0) valueBlue = 0;

                        data = (FastBitmap.PixelData*)(processor_out.pBase + y * processor_out.width + x * pdsize);
                        data->red = (byte)(valueRed);
                        data->green = (byte)(valueGreen);
                        data->blue = (byte)(valueBlue);


                    }
                }
                else
                {


                    for (int x = 0; x < iW; x++)
                    {


                        valueBlue = valueGreen = valueRed = 0;
                        for (int i = startY, j = start; i <= stopY; i++, j++)
                        {
                            index = (processor.pBase + i * processor.width + x * pdsize);
                            tmp = tmpContrib[j];
                            valueRed += (((FastBitmap.PixelData*)(index)))->red * tmp;
                            valueGreen += (((FastBitmap.PixelData*)(index)))->green * tmp;
                            valueBlue += (((FastBitmap.PixelData*)(index)))->blue * tmp;

                        }

                        if (valueRed > 255) valueRed = 255;
                        else if (valueRed < 0) valueRed = 0;

                        if (valueGreen > 255) valueGreen = 255;
                        else if (valueGreen < 0) valueGreen = 0;

                        if (valueBlue > 255) valueBlue = 255;
                        else if (valueBlue < 0) valueBlue = 0;

                        data = (FastBitmap.PixelData*)(processor_out.pBase + y * processor_out.width + x * pdsize);
                        data->red = (byte)(valueRed);
                        data->green = (byte)(valueGreen);
                        data->blue = (byte)(valueBlue);
                    }
                }

            }
            processor.UnlockImage();
            processor_out.UnlockImage();

            return pbOut;

        }


        static int pdsize = sizeof(FastBitmap.PixelData);



        static Bitmap HorizontalFiltering(Bitmap bufImage, int iOutW)
        {
            int dwInW = bufImage.Width;
            int dwInH = bufImage.Height;


            double valueRed = 0.0;
            double valueGreen = 0.0;
            double valueBlue = 0.0;


            Bitmap pbOut = new Bitmap(iOutW, dwInH, PixelFormat.Format24bppRgb);

            FastBitmap processor = new FastBitmap(bufImage);
            processor.LockImage();

            FastBitmap processor_out = new FastBitmap(pbOut);
            processor_out.LockImage();

            byte* index;
            FastBitmap.PixelData* data;



            double tmp = 0.0;
            for (int x = 0; x < iOutW; x++)
            {

                int startX;
                int start;
                int X = (int)(((double)x) * ((double)dwInW) / ((double)iOutW) + 0.5);
                int y = 0;

                startX = X - nHalfDots;
                if (startX < 0)
                {
                    startX = 0;
                    start = nHalfDots - X;
                }
                else
                {
                    start = 0;
                }

                int stop;
                int stopX = X + nHalfDots;
                if (stopX > (dwInW - 1))
                {
                    stopX = dwInW - 1;
                    stop = nHalfDots + (dwInW - 1 - X);
                }
                else
                {
                    stop = nHalfDots * 2;
                }

                if (start > 0 || stop < nDots - 1)
                {
                    CalTempContrib(start, stop);
                    for (y = 0; y < dwInH; y++)
                    {
                        int i, j;
                        valueBlue = valueGreen = valueRed = 0;
                        for (i = startX, j = start; i <= stopX; i++, j++)
                        {

                            index = (processor.pBase + y * processor.width + i * pdsize);
                            tmp = tmpContrib[j];

                            valueRed += (((FastBitmap.PixelData*)(index)))->red * tmp;
                            valueGreen += (((FastBitmap.PixelData*)(index)))->green * tmp;
                            valueBlue += (((FastBitmap.PixelData*)(index)))->blue * tmp; // tmpContrib[j]

                        }

                        if (valueRed > 255) valueRed = 255;
                        else if (valueRed < 0) valueRed = 0;

                        if (valueGreen > 255) valueGreen = 255;
                        else if (valueGreen < 0) valueGreen = 0;

                        if (valueBlue > 255) valueBlue = 255;
                        else if (valueBlue < 0) valueBlue = 0;


                        data = (FastBitmap.PixelData*)(processor_out.pBase + y * processor_out.width + x * pdsize);
                        data->red = (byte)(valueRed);
                        data->green = (byte)(valueGreen);
                        data->blue = (byte)(valueBlue);
                    }
                }
                else
                {

                    for (y = 0; y < dwInH; y++)
                    {

                        int i, j;
                        valueBlue = valueGreen = valueRed = 0;
                        for (i = startX, j = start; i <= stopX; i++, j++)
                        {
                            index = processor.pBase + y * processor.width + i * pdsize;
                            tmp = tmpContrib[j];
                            valueRed += (((FastBitmap.PixelData*)(index)))->red * tmp;
                            valueGreen += (((FastBitmap.PixelData*)(index)))->green * tmp;
                            valueBlue += (((FastBitmap.PixelData*)(index)))->blue * tmp;

                        }

                        if (valueRed > 255) valueRed = 255;
                        else if (valueRed < 0) valueRed = 0;

                        if (valueGreen > 255) valueGreen = 255;
                        else if (valueGreen < 0) valueGreen = 0;

                        if (valueBlue > 255) valueBlue = 255;
                        else if (valueBlue < 0) valueBlue = 0;

                        data = (FastBitmap.PixelData*)(processor_out.pBase + y * processor_out.width + x * pdsize);
                        data->red = (byte)(valueRed);
                        data->green = (byte)(valueGreen);
                        data->blue = (byte)(valueBlue);


                    }

                }

            }

            processor.UnlockImage();
            processor_out.UnlockImage();
            return pbOut;

        }

        //----------------------------- 傳說中的 Lanczos3 縮圖演算法 ,待速度優化 end


        ////////////////////////////////////////////////



        static Bitmap rgbImage = null;

        static void ImageToBitMap()
        {

            Console.WriteLine("\nBitmap物件轉化中..");
            rgbImage = new Bitmap(width, height, PixelFormat.Format24bppRgb);

            //c#的Bitmap讀寫效率太差,改換別人寫好的高效率fastbitmap加速處理
            //http://www.vcskicks.com/fast-image-processing.php

            FastBitmap processor = new FastBitmap(rgbImage);
            processor.LockImage();

            /*
             * for ( int i = 2 ; i < width+2 ; i++)
             *  for (int j = height_m; j < raw_height - height_m ; j++)
             *  原始迴圈
             *  這裡使用多核心平行處理
             */
            Parallel.For(2, (width + 2) - 1, i =>
            {
                Parallel.For(height_m, (raw_height - height_m) - 1, j =>
                 {

                     //多數可換鏡頭的機種拍攝出來的rw2檔為12bits,所以要剪裁掉4個bits >>4
                     //不過如果是dc拍出來的多為10bits,只需要剪裁掉2個bits ,程式需要改成 >> 2
                     //這地方rw2檔有定義紀錄,但是讀取哪個值要確認一下

                     //由於經過白平衡,從12bits上限延伸到16bits上限,因此降到8bits時要位移8bits
                     processor.SetPixel(i - 2, j - height_m, Color.FromArgb(demosaicing_image[i - 2, j - height_m].r >> 8, demosaicing_image[i - 2, j - height_m].g >> 8, demosaicing_image[i - 2, j - height_m].b >> 8));


                 });
            });

            processor.UnlockImage();
            Console.WriteLine("Bitmap物件轉化結束.");

        }


        /*
         * 利用Camera拍攝時記錄的白平衡設定參數來進行白平衡處理
         */
        static void ImageWhitebalanceCameraSetup()
        {
            /*
             * 每一台相機raw檔白平衡修正參數的得取方式不一定一樣
             * 每一台相機白平衡修正參數的使用方式不一定一樣
             * 這裡的處理流程僅只適用於多數panasonic raw檔
             * 以Panasonic來說會有三個有效參數分別對應RGB三色修正
             * 修正的方式就是把R & G & B 分別成上三個修正參數
             * 然後做CLIP處理,不過在做相乘前還要對這三個參數進行正規化修正處理
             */

            UInt32 c;
            double[] dsum;
            double dmin, dmax;

            scale_mul = new float[4];
            dsum = new double[8];

            pre_mul[0] = cam_mul[0];
            pre_mul[1] = cam_mul[1];
            pre_mul[2] = cam_mul[2];
            pre_mul[3] = cam_mul[1];// 0~3 4個參數,第3個參數永遠為0,dcraw中以第1個參數替補



            for (dmin = double.MaxValue, dmax = c = 0; c < 4; c++)
            {
                if (dmin > pre_mul[c])
                    dmin = pre_mul[c];
                if (dmax < pre_mul[c])
                    dmax = pre_mul[c];
            }
            dmax = dmin; // 尋找這幾組參數中最小的

            maximum = 3986; // 12bits 內定最大值

            for (c = 0; c < 4; c++)
                scale_mul[c] = (float)((pre_mul[c] /= (float)dmax) * 65535.0 / maximum);
            //計算出正規化後的相乘修正參數　

        }


        /*
         * 自動計算白平衡 Histogram Equalization
         * 參考 http://pastebin.com/PnsuWgXh
         */
        static void HistogramEqualization()
        {

            Console.WriteLine("\nHistogram Equalization處理中..");

            int[] hist = new int[256];
            int sum = 0;
            int[] sum_of_hist = new int[256];
            int area = height * width;
            double constant = 255.0 / (double)area;

            byte* index;

            FastBitmap processor = new FastBitmap(rgbImage);
            processor.LockImage();

            for (int i = 0; i < width; i++)
                for (int j = 0; j < height; j++)
                {
                    index = (processor.pBase + j * processor.width + i * pdsize);
                    int r = (((FastBitmap.PixelData*)(index)))->red;
                    int g = (((FastBitmap.PixelData*)(index)))->green;
                    int b = (((FastBitmap.PixelData*)(index)))->blue;


                    int avr = (r + g + b) / 3;
                    hist[avr] = hist[avr] + 1;
                }

            for (int i = 0; i < 256; i++)
            {
                sum = sum + hist[i];
                sum_of_hist[i] = sum;
            }

            for (int i = 0; i < width; i++)

                for (int j = 0; j < height; j++)
                {

                    index = (processor.pBase + j * processor.width + i * pdsize);
                    int r = (((FastBitmap.PixelData*)(index)))->red;
                    int g = (((FastBitmap.PixelData*)(index)))->green;
                    int b = (((FastBitmap.PixelData*)(index)))->blue;

                    (((FastBitmap.PixelData*)(index)))->red = (byte)(constant * sum_of_hist[r]);
                    (((FastBitmap.PixelData*)(index)))->green = (byte)(constant * sum_of_hist[g]);
                    (((FastBitmap.PixelData*)(index)))->blue = (byte)(constant * sum_of_hist[b]);
                }

            processor.UnlockImage();
            Console.WriteLine("Histogram Equalization處理結束.");


        }


        /*
         * 自動計算白平衡
         * 參考如下
         * http://read.pudn.com/downloads46/sourcecode/graph/texture_mapping/153088/WhiteBalance/autowb/autowb.cpp__.htm
         * 灰階延伸法
         */
        static void ImageWhitebalanceAuto_grayworld()
        {

            Console.WriteLine("\n白平衡處理中..");

            double YSum = 0;
            double CbSum = 0;
            double CrSum = 0;
            double n = 0;

            double R, B, G, Y, Cb, Cr;
            double a11, a12, a21, a22, b1, b2, Ar, Ab;

            FastBitmap processor = new FastBitmap(rgbImage);
            processor.LockImage();

            byte* index;

            for (int i = 1; i < width; i++)
                for (int j = 0; j < height; j++)
                {

                    index = (processor.pBase + j * processor.width + i * pdsize);

                    R = (((FastBitmap.PixelData*)(index)))->red;
                    G = (((FastBitmap.PixelData*)(index)))->green;
                    B = (((FastBitmap.PixelData*)(index)))->blue;

                    Y = 0.299 * R + 0.587 * G + 0.114 * B;
                    Cb = -0.1687 * R - 0.3313 * G + 0.5 * B;
                    Cr = 0.5 * R - 0.4187 * G - 0.0813 * B;


                    if (Y - Math.Abs(Cb) - Math.Abs(Cr) > 0) // 門檻值可以調整,不過基本上預設成0是ok的
                    {
                        YSum += Y;
                        CbSum += Cb;
                        CrSum += Cr;
                        n++;
                    }
                }

            if (n == 0)
            {
                return;
            }

            YSum /= n;
            CbSum /= n;
            CrSum /= n;

            Y = YSum;
            Cb = CbSum;
            Cr = CrSum;

            a11 = -0.1687 * Y - 0.2365 * Cr;
            a12 = 0.5 * Y + 0.866 * Cb;
            a21 = 0.5 * Y + 0.701 * Cr;
            a22 = -0.0813 * Y - 0.1441 * Cb;

            b1 = 0.3313 * Y - 0.114 * Cb - 0.2366 * Cr;
            b2 = 0.4187 * Y - 0.1441 * Cb - 0.299 * Cr;


            Ar = (a22 * b1 - a12 * b2) / (a11 * a22 - a12 * a21);
            Ab = (a21 * b1 - a11 * b2) / (a21 * a12 - a11 * a22);

            for (int i = 1; i < width; i++)
                for (int j = 0; j < height; j++)
                {
                    index = (processor.pBase + j * processor.width + i * pdsize);

                    R = (((FastBitmap.PixelData*)(index)))->red;
                    G = (((FastBitmap.PixelData*)(index)))->green;
                    B = (((FastBitmap.PixelData*)(index)))->blue;

                    R *= Ar;
                    if (R > 255)
                        R = 255;


                    B *= Ab;
                    if (B > 255)
                        B = 255;

                    (((FastBitmap.PixelData*)(index)))->red = (byte)R;
                    (((FastBitmap.PixelData*)(index)))->green = (byte)G;
                    (((FastBitmap.PixelData*)(index)))->blue = (byte)B;
                }

            processor.UnlockImage();

            Console.WriteLine("白平衡處理結束.");
        }



        /*
         * http://en.wikipedia.org/wiki/Demosaicing
         * 程式使用演算法參考 http://www.siliconimaging.com/RGB%20Bayer.htm 最簡單的內插法
         * 有時候對比比較大的邊界處會有鋸齒問題
         */
        static void raw_demosaicing()
        {

            Console.WriteLine("\ndemosaicing...");

            UInt32 r = 0, g = 0, b = 0;


            //有些電腦在這裡使用平行計算會產生錯亂狀況,但是我家電腦正常
            //不過基於穩定性,這地方也沒佔多少時間,因此改成一般處理
            for (int i = height_m; i < raw_height - height_m; i++)
                for (int j = 2; j < width + 2; j++)
                {
                    {


                        /*
                         * 這裡的寫法並不是很理想,使用cow與col數字積偶與大小來判斷內插處理的序列,使用patten filter表來對照會比較好,
                         * 適對應patten來切換bayer filter,也由於直接用程式寫死,因此有些 rw2 檔解出來色彩會不正確或是序列錯誤,
                         * 其實 rw2 內有附帶資訊,告知使用哪種bayer patten,詳細參考dcraw.
                         */



                        //這裡的邏輯需要重新檢查確一下
                        //不過結果似乎沒啥問題....神奇
                        if (filters == 2 || filters == 3)
                        {

                            //處理g
                            if (Math.Abs((int)(i - height_m) - (int)(j - 2)) % 2 == 0)
                                g = raw_image[i + 1, j + 1];
                            else
                                g = (UInt16)((raw_image[i, j + 1] + raw_image[i + 2, j + 1] + raw_image[i + 1, j + 2] + raw_image[i + 1, j]) / 4.0);

                            //處理b
                            if ((j - 2) % 2 == 0 && (i - height_m) % 2 == 1)
                                b = raw_image[i + 1, j + 1];
                            else if ((i - height_m) % 2 == 0 && (j - 2) % 2 == 1)
                                b = (UInt16)((raw_image[i, j] + raw_image[i, j + 2] + raw_image[i + 2, j] + raw_image[i + 2, j + 2]) / 4.0);
                            else if (Math.Abs((int)i - (int)(j - 2)) % 2 == 0 && (i - height_m) % 2 == 0)
                                b = (UInt16)((raw_image[i, j + 1] + raw_image[i + 2, j + 1]) / 2.0);
                            else if (Math.Abs((int)(i - height_m) - (int)(j - 2)) % 2 == 0 && (i - height_m) % 2 == 1)
                                b = (UInt16)((raw_image[i + 1, j] + raw_image[i + 1, j + 2]) / 2.0);

                            //處理r
                            if ((i - height_m) % 2 == 0 && (j - 2) % 2 == 1)
                                r = raw_image[i + 1, j + 1];
                            else if ((j - 2) % 2 == 0 && (i - height_m) % 2 == 1)
                                r = (UInt16)((raw_image[i, j] + raw_image[i, j + 2] + raw_image[i + 2, j] + raw_image[i + 2, j + 2]) / 4.0);
                            else if (Math.Abs((int)(i - height_m) - (int)(j - 2)) % 2 == 0 && (i - height_m) % 2 == 1)
                                r = (UInt16)((raw_image[i, j + 1] + raw_image[i + 2, j + 1]) / 2.0);
                            else if (Math.Abs((int)(i - height_m) - (int)(j - 2)) % 2 == 0 && (i - height_m) % 2 == 0)
                                r = (UInt16)((raw_image[i + 1, j] + raw_image[i + 1, j + 2]) / 2.0);


                            if (filters == 2)
                            {
                                UInt32 tmp = r;
                                r = b;
                                b = tmp;
                            }
                        }

                        //type 1 & 4 邏輯規則跟 2 & 3剛好相反,先確定2&3無誤,再繼續實作1&4
                        if (filters == 1 || filters == 4)
                        {

                        }

                        r = (UInt32)(((float)r) * scale_mul[0]);
                        g = (UInt32)(((float)g) * scale_mul[1]);
                        b = (UInt32)(((float)b) * scale_mul[2]);


                        // if (r > 4095) r = 4095;
                        // if (g > 4095) g = 4095;
                        // if (b > 4095) b = 4095;

                        //經過白平衡修正參數處理,已經將範圍正規化到16bits
                        //因此上限最大值改為65535

                        if (r > 65535) r = 65535;
                        if (g > 65535) g = 65535;
                        if (b > 65535) b = 65535;

                        if (r < 0) r = 0;
                        if (g < 0) g = 0;
                        if (b < 0) b = 0;

                        demosaicing_image[j - 2, i - height_m] = new rgb();
                        demosaicing_image[j - 2, i - height_m].r = (ushort)r;
                        demosaicing_image[j - 2, i - height_m].g = (ushort)g;
                        demosaicing_image[j - 2, i - height_m].b = (ushort)b;


                    }

                }

            Console.WriteLine("demosaicing done.");

        }
        #endregion
        #region export tool
        static void RawImageExport_CSV()
        {
            Console.WriteLine("\nraw檔資料匯出中..");
            StreamWriter output = new StreamWriter(ifname + ".csv");

            for (int row = 0; row < raw_height; row++)
            {
                for (int col = 0; col < raw_width; col++)
                {
                    output.Write(String.Format("{0:0000}", raw_image[row, col]));
                    if (col != raw_width - 1)
                        output.Write(",");
                }
                output.Write("\n");
            }
            output.Close();
            Console.WriteLine("raw檔資料匯出完成.");
        }
        #endregion
        #region raw paser
        static void write_RAW(int row, int col, ushort tmp)
        {
            raw_image[row, col] = tmp;
        }

        static byte[] buf = new byte[0x4001]; // 原始C code是 0x4000,但是不知道為啥,在C#會暴掉,需要多增加1
        static int vbits = 0;

        static UInt32 pana_bits(int nbits)
        {

            Int32 byte_c;
            if (nbits == 0)
                return (UInt32)(vbits = 0);
            if (vbits == 0)
            {
                ifp.Read(buf, (int)load_flags, (int)(0x4000 - load_flags));
                ifp.Read(buf, 0, (int)load_flags);
            }
            vbits = (vbits - nbits) & 0x1ffff;
            byte_c = (Int32)((UInt32)(vbits >> 3) ^ (UInt32)0x3ff0);
            return (UInt32)((buf[byte_c] | buf[byte_c + 1] << 8) >> (vbits & 7) & ~(-1 << nbits));

        }

        static void panasonic_load_raw()
        {
            Console.WriteLine("\nRAW檔資料paser處理中..");

            int row, col, i, j, sh = 0;
            int[] nonz = new int[2];
            int[] pred = new int[2];
            pana_bits(0);
            for (row = 0; row < raw_height; row++)
                for (col = 0; col < raw_width; col++)
                {
                    if ((i = col % 14) == 0)
                        pred[0] = pred[1] = nonz[0] = nonz[1] = 0;
                    if (i % 3 == 2)
                        sh = 4 >> (3 - (int)(pana_bits(2)));

                    if (nonz[i & 1] != 0)
                    {
                        if (((j = ((int)pana_bits(8)))) != 0)
                        {
                            if ((pred[i & 1] -= 0x80 << sh) < 0 || sh == 4)
                                pred[i & 1] &= ~(-1 << sh);
                            pred[i & 1] += j << sh;
                        }
                    }
                    else if (((nonz[i & 1] = (int)pana_bits(8))) > 0 || i > 11)
                        pred[i & 1] = (int)((uint)nonz[i & 1] << 4 | pana_bits(4));
                    write_RAW(row, col, (ushort)pred[col & 1]);
                }
            Console.WriteLine("RAW檔資料paser結束.");
        }
        #endregion
        #region bitwise tool
        static ushort get2()
        {
            byte[] str = new byte[2] { 0xff, 0xff };
            ifp.Read(str, 0, 2);
            if (order == 0x4949)
                return (ushort)(str[0] | str[1] << 8);
            else
                return (ushort)(str[0] << 8 | str[1]);
        }
        static UInt32 get4()
        {
            byte[] str = new byte[4] { 0xff, 0xff, 0xff, 0xff };
            ifp.Read(str, 0, 4);
            if (order == 0x4949)
                return (UInt32)(str[0] | str[1] << 8 | str[2] << 16 | str[3] << 24);
            else
                return (UInt32)(str[0] << 24 | str[1] << 16 | str[2] << 8 | str[3]);
        }

        static UInt32 getint(int type)
        {
            return type == 3 ? get2() : get4();
        }
        #endregion
        #region tiff paser
        static void tiff_get(UInt32 base_c, ref UInt32 tag, ref UInt32 type, ref UInt32 len, ref UInt32 save)
        {
            tag = get2();
            type = get2();
            len = get4();
            save = (UInt32)(ifp.Position + 4);
            if (len * ("11124811248488"[type < 14 ? (int)type : 0] - '0') > 4)
                ifp.Seek(get4() + base_c, SeekOrigin.Begin);
        }

        static void parse_exif(int base_c)
        {
            UInt32 entries, tag = 0, type = 0, len = 0, save = 0;
            entries = get2();
            while (entries-- > 0)
            {
                tiff_get((UInt32)base_c, ref tag, ref type, ref len, ref save);
                ifp.Seek(save, SeekOrigin.Begin);
            }
        }

        static int parse_tiff(int base_c)
        {
            int doff;
            ifp.Seek(base_c, SeekOrigin.Begin);

            order = (short)(get2());
            get2();

            while ((doff = (int)get4()) > 0)
            {
                ifp.Seek(doff + base_c, SeekOrigin.Begin);
                if (parse_tiff_ifd(base_c) > 0)
                    break;
            }
            return 1;
        }

        static void identify()
        {
            Console.WriteLine("\nRAW檔資訊讀取中..");
            int hlen, flen, fsize;
            byte[] head = new byte[32];
            tiff_nifds = 0;
            order = (short)get2();
            hlen = (int)get4();
            ifp.Seek(0, SeekOrigin.Begin);
            ifp.Read(head, 0, 32);
            ifp.Seek(0, SeekOrigin.End);
            flen = fsize = (int)ifp.Position;
            parse_tiff(0);
            apply_tiff();
            Console.WriteLine("RAW檔資訊讀取結束.");
        }
        static int parse_tiff_ifd(int base_c)
        {

            tiff_ifd_c.Add(new tiff_ifd());
            UInt32 entries = 0, tag = 0, type = 0, len = 0, save = 0;
            int ifd;

            byte[] software = new byte[64];
            ifd = (int)tiff_nifds++;
            entries = get2();
            while (entries-- > 0)
            {
                tiff_get((UInt32)base_c, ref tag, ref  type, ref  len, ref  save);
                switch (tag)
                {
                    case 5: width = get2();
                        break;
                    case 6: height = get2();
                        break;
                    case 7: width += get2();
                        break;
                    case 9: filters = get2();
                        break;
                    case 23:
                        if (type == 3)
                            get2();
                        break;
                    case 36:
                    case 37:
                    case 38:
                        {
                            cam_mul[tag - 0x24] = get2();
                            //Console.WriteLine("{0} {1} ", tag - 0x24, cam_mul[tag - 0x24]);
                            //rw2檔的白平衡設定參數於此,尚不知道如何使用
                        }
                        break;
                    case 39:
                        break;
                    case 46:
                        if (type != 7 || ifp.ReadByte() != 0xff || ifp.ReadByte() != 0xd8) break;
                        thumb_offset = (uint)(ifp.Position - 2);
                        thumb_length = len;
                        break;
                    case 2:
                    case 256:
                    case 61441:
                        tiff_ifd_c[ifd].width = (int)getint((int)type);
                        break;
                    case 3:
                    case 257:
                    case 61442:
                        tiff_ifd_c[ifd].height = (int)getint((int)type);
                        break;
                    case 271:
                        ifp.Read(make, 0, 64);
                        break;
                    case 272:
                        ifp.Read(model, 0, 64);
                        break;
                    case 280:				/* Panasonic RW2 offset */
                        if (type != 4)
                            break;
                        load_flags = 0x2008;
                        goto case 61447;
                    case 273:
                    case 513:
                    case 61447:
                        tiff_ifd_c[ifd].offset = (int)(get4() + base_c);
                        if (tiff_ifd_c[ifd].bps == 0 && tiff_ifd_c[ifd].offset > 0)
                            ifp.Seek(tiff_ifd_c[ifd].offset, SeekOrigin.Begin);
                        break;
                    case 274:
                        tiff_ifd_c[ifd].flip = "50132467"[get2() & 7] - '0';
                        break;
                    case 279:
                    case 514:
                    case 61448:
                        tiff_ifd_c[ifd].bytes = (int)get4();
                        break;
                    case 305:
                    case 11:
                        ifp.Read(software, 0, 64);
                        break;
                    case 34665:
                        ifp.Seek(get4() + base_c, SeekOrigin.Begin);
                        parse_exif(base_c);
                        break;
                }
                ifp.Seek(save, SeekOrigin.Begin);
            }
            return 0;
        }

        class tiff_ifd
        {
            public int width = 0, height = 0, bps = 0, comp = 0, phint = 0, offset = 0, flip = 0, samples = 0, bytes = 0;
            public int tile_width = 0, tile_length = 0;
        } ;

        static List<tiff_ifd> tiff_ifd_c = new List<tiff_ifd>();

        static void apply_tiff()
        {
            int i;
            if (thumb_offset > 0) ifp.Seek(thumb_offset, SeekOrigin.Begin);

            for (i = 0; i < tiff_nifds; i++)
            {
                if ((tiff_ifd_c[i].comp != 6 || tiff_ifd_c[i].samples != 3) &&
                    (tiff_ifd_c[i].width | tiff_ifd_c[i].height) < 0x10000 &&
                    tiff_ifd_c[i].width * tiff_ifd_c[i].height > raw_width * raw_height)
                {
                    raw_width = (ushort)tiff_ifd_c[i].width;
                    raw_height = (ushort)tiff_ifd_c[i].height;
                    tiff_bps = (uint)tiff_ifd_c[i].bps;

                    tiff_compress = (uint)tiff_ifd_c[i].comp;
                    data_offset = (uint)tiff_ifd_c[i].offset;
                    tiff_flip = tiff_ifd_c[i].flip;
                    tiff_samples = (uint)tiff_ifd_c[i].samples;
                    tile_width = (uint)tiff_ifd_c[i].tile_width;
                    tile_length = (uint)tiff_ifd_c[i].tile_length;
                }
            }
        }
        #endregion

    }
}
