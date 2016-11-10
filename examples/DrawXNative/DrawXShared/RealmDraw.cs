﻿////////////////////////////////////////////////////////////////////////////
//
// Copyright 2014 Realm Inc.
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
// http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.
//
////////////////////////////////////////////////////////////////////////////

using System.Diagnostics;
using SkiaSharp;
using Realms;

namespace DrawXShared
{
    public class RealmDraw
    {
        private bool _isDrawing = false;
        private Realm _realm;
        private DrawPath _drawPath;
        private SwatchColor _currentColor = SwatchColor.Flamingo;


        public RealmDraw()
        {
            // TODO close the Realm
            _realm = Realm.GetInstance("DrawX.realm");
        }

        public void DrawBackground(SKCanvas canvas, int width, int height)
        {
            canvas.Clear(SKColors.White);

            using (SKPaint paint = new SKPaint())
            using (SKPath path = new SKPath())
            {
                paint.Style = SKPaintStyle.Stroke;
                paint.StrokeWidth = 10;
                paint.IsAntialias = true;
                paint.StrokeCap = SKStrokeCap.Round;
                paint.StrokeJoin = SKStrokeJoin.Round;
                paint.Color = SwatchColor.Dove.color;//RealmDrawerMedia.Colors.XamarinGreen;

                path.MoveTo(20, 20);
                path.LineTo(400, 50);
                path.LineTo(80, 100);
                path.LineTo(300, 150);

                canvas.DrawPath(path, paint);
            }
        }

        // replaces the CanvasView.drawRect of the original
        public void DrawTouches(SKCanvas canvas, int width, int height)
        {
            // TODO avoid clear and build up new paths incrementally fron the unfinished ones
            canvas.Clear(SKColors.White);

            using (SKPaint paint = new SKPaint())
            {
                paint.Style = SKPaintStyle.Stroke;
                paint.StrokeWidth = 10;
                paint.IsAntialias = true;
                paint.StrokeCap = SKStrokeCap.Round;
                paint.StrokeJoin = SKStrokeJoin.Round;
                foreach (var drawPath in _realm.All<DrawPath>())
                {
                    using (SKPath path = new SKPath())
                    {
                        var pathColor = SwatchColor.colors[drawPath.color].color;
                        paint.Color = pathColor;
                        bool isFirst = true;
                        foreach (var point in drawPath.points)
                        {
                            // for compatibility with iOS Realm, stores doubles
                            float fx = (float)point.x;
                            float fy = (float)point.y;
                            if (isFirst)
                            {
                                isFirst = false;
                                path.MoveTo(fx, fy);
                            }
                            else
                            {
                                path.LineTo(fx, fy);
                            }
                        }
                        canvas.DrawPath(path, paint);
                    }
                }
            } // SKPaint
        }


        public void StartDrawing(double inX, double inY)
        {
            _isDrawing = true;
            _realm.Write(() =>
            {
                _drawPath = new DrawPath() { color = _currentColor.name };
                _drawPath.points.Add(new DrawPoint() { x = inX, y = inY });
                _realm.Manage(_drawPath);
            });
        }

        public void AddPoint(double inX, double inY)
        {
            Debug.Assert(_isDrawing = true);
            //TODO add check if _drawPath.IsInvalidated
            _realm.Write(() =>
            {
                _drawPath.points.Add(new DrawPoint() { x = inX, y = inY });
            });
        }
        public void StopDrawing(double inX, double inY)
        {
            _realm.Write(() =>
            {
                _drawPath.points.Add(new DrawPoint() { x = inX, y = inY });
                _drawPath.drawerID = "";  // TODO work out what the intent is here in original Draw sample!
            });
            _isDrawing = false;
        }

        public void CancelDrawing()
        {
            _isDrawing = false;
            // TODO wipe current path
        }

        public void ErasePaths()
        {
            _realm.Write(() => _realm.RemoveAll<DrawPath>());
        }
    }
}