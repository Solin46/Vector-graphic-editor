using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Microsoft.Win32;

namespace VectorEditor
{
    public partial class MainWindow : Window
    {
        #region Переменные и константы

        private enum ToolMode {Rectangle, Ellipse, Line, Polygon }
        private enum EditorMode { Creating, Editing }

        private ToolMode currentTool = ToolMode.Rectangle;
        private EditorMode currentMode = EditorMode.Creating;

        private Shape currentShape;
        private Point startPoint;
        private bool isCreating = false;
        private bool isMoving = false;
        private bool isScaling = false;
        private Point moveStartPoint;
        private Point scaleStartPoint;
        private Shape selectedShape;

        private Color fillColor = Colors.LightGray;
        private Color strokeColor = Colors.LightGray;
        private double strokeThickness = 2;

        private Queue<EditorAction> undoQueue = new Queue<EditorAction>(); // очередь для отмены
        private const int MAX_UNDO_STEPS = 10; // последние 10 действий

        private List<Point> polygonPoints = new List<Point>();
        private bool isCreatingPolygon = false;

        private bool wasActuallyMoved = false;

        private List<Rectangle> resizeHandles = new List<Rectangle>();
        private ShapePosition initialPosition;

        // Ограничения масштабирования
        private const double MIN_SCALE = 0.3;
        private const double MAX_SCALE = 3.0;

        // Размер маркеров относительно размера фигуры
        private const double HANDLE_SIZE_RATIO = 0.1;
        private const double MIN_HANDLE_SIZE = 6;
        private const double MAX_HANDLE_SIZE = 12;
        private int activeHandleIndex = -1; // переменная для хранения активного маркера

        // Предустановленные цвета
        private readonly (string Name, Color Color)[] fillColors = new[]
        {
            ("Белый", Colors.White),
            ("Серый", Colors.Gray),
            ("Черный", Colors.Black),
            ("Фиолетовый", Colors.Purple),
            ("Синий", Colors.Blue),
            ("Голубой", Colors.LightBlue),
            ("Зеленый", Colors.Green),
            ("Салатовый", Colors.LightGreen),
            ("Желтый", Colors.Yellow),
            ("Оранжевый", Colors.Orange),
            ("Красный", Colors.Red),
            ("Розовый", Colors.Pink)
        };

        private readonly (string Name, Color Color)[] strokeColors = new[]
        {
            ("Фиолетовый", Colors.Purple),
            ("Синий", Colors.Blue),
            ("Голубой", Colors.LightBlue),
            ("Зеленый", Colors.Green),
            ("Салатовый", Colors.LightGreen),
            ("Желтый", Colors.Yellow),
            ("Оранжевый", Colors.Orange),
            ("Красный", Colors.Red),
            ("Коричневый", Colors.Brown),
            ("Черный", Colors.Black),
            ("Серый", Colors.Gray),
            ("Белый", Colors.White)
        };

        #endregion

        #region Классы для системы отмены

        public class EditorAction
        {
            public string Type { get; set; } // тип действия, применяющегося к фигуре
            public Shape TargetShape { get; set; }
            public object OldValue { get; set; }
            public object NewValue { get; set; }
            public string PropertyName { get; set; }
            public ShapePosition FullState { get; set; } // Полное состояние фигуры при удалении
        }

        public class ShapePosition
        {
            public double X { get; set; }
            public double Y { get; set; }
            public double Width { get; set; }
            public double Height { get; set; }
            public List<Point> Points { get; set; }
        }

        #endregion

        public MainWindow()
        {
            InitializeComponent();
            this.Loaded += MainWindow_Loaded;
        }

        #region Инициализация

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                shapeComboBox.SelectedIndex = 0;
                zoomSlider.Value = 1.0;
                if (zoomText != null)
                    zoomText.Text = "100%";

                UpdateColorButtons();

                shapeComboBox.SelectionChanged += ShapeComboBox_SelectionChanged;
                zoomSlider.ValueChanged += ZoomSlider_ValueChanged;
                drawModeRadio.Checked += DrawModeRadio_Checked;
                editModeRadio.Checked += EditModeRadio_Checked;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Ошибка загрузки: {ex.Message}");
            }
        }

        private void UpdateColorButtons()
        {
            if (fillColorButton != null)
            {
                fillColorButton.Background = new SolidColorBrush(fillColor);
                fillColorButton.Foreground = GetContrastColor(fillColor);

                if (currentMode == EditorMode.Editing && selectedShape != null)
                    fillColorButton.ToolTip = "Изменить цвет заливки ВЫБРАННОЙ фигуры";
                else
                    fillColorButton.ToolTip = "Установить цвет заливки для НОВЫХ фигур";
            }

            if (strokeColorButton != null)
            {
                strokeColorButton.Background = new SolidColorBrush(strokeColor);
                strokeColorButton.Foreground = GetContrastColor(strokeColor);

                if (currentMode == EditorMode.Editing && selectedShape != null)
                    strokeColorButton.ToolTip = "Изменить цвет обводки ВЫБРАННОЙ фигуры";
                else
                    strokeColorButton.ToolTip = "Установить цвет обводки для НОВЫХ фигур";
            }
        }

        private Brush GetContrastColor(Color color)
        {
            double luminance = (0.299 * color.R + 0.587 * color.G + 0.114 * color.B) / 255;
            return luminance > 0.5 ? Brushes.Black : Brushes.White;
        }

        #endregion

        #region Обработчики режимов

        private void DrawModeRadio_Checked(object sender, RoutedEventArgs e)
        {
            currentMode = EditorMode.Creating;
            DeselectShape();
            UpdateColorButtons();
        }

        private void EditModeRadio_Checked(object sender, RoutedEventArgs e)
        {
            currentMode = EditorMode.Editing;
            UpdateColorButtons();
        }

        private void ShapeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (shapeComboBox.SelectedIndex == 0) currentTool = ToolMode.Rectangle;
            else if (shapeComboBox.SelectedIndex == 1) currentTool = ToolMode.Ellipse;
            else if (shapeComboBox.SelectedIndex == 2) currentTool = ToolMode.Line;
            else if (shapeComboBox.SelectedIndex == 3) currentTool = ToolMode.Polygon;
        }

        #endregion

        #region Масштабирование холста

        private void ZoomInButton_Click(object sender, RoutedEventArgs e)
        {
            if (zoomSlider == null) return;
            zoomSlider.Value = Math.Min(zoomSlider.Maximum, zoomSlider.Value + 0.1);
            if (zoomText != null)
                zoomText.Text = $"{zoomSlider.Value * 100:0}%";
        }

        private void ZoomOutButton_Click(object sender, RoutedEventArgs e)
        {
            if (zoomSlider == null) return;
            zoomSlider.Value = Math.Max(zoomSlider.Minimum, zoomSlider.Value - 0.1);
            if (zoomText != null)
                zoomText.Text = $"{zoomSlider.Value * 100:0}%";
        }

        private void ZoomResetButton_Click(object sender, RoutedEventArgs e)
        {
            if (zoomSlider == null) return;
            zoomSlider.Value = 1.0;
            if (zoomText != null)
                zoomText.Text = "100%";
        }

        private void ZoomSlider_ValueChanged(object sender, RoutedEventArgs e)
        {
            if (zoomSlider == null) return;

            double scale = zoomSlider.Value;
            if (zoomText != null)
                zoomText.Text = $"{scale * 100:0}%";

            if (mainCanvas != null)
            {
                var transform = new ScaleTransform(scale, scale);
                mainCanvas.LayoutTransform = transform;
            }
        }

        #endregion

        #region Обработка мыши

        private void MainCanvas_MouseDown(object sender, MouseButtonEventArgs e)
        {
            var position = e.GetPosition(mainCanvas);

            if (currentMode == EditorMode.Editing)
            {
                var element = mainCanvas.InputHitTest(position) as FrameworkElement;
                if (element is Rectangle handle && resizeHandles.Contains(handle))
                {
                    isScaling = true;
                    scaleStartPoint = position;
                    SaveInitialPosition(selectedShape);
                    e.Handled = true;
                    return;
                }

                if (element is Shape shape)
                {
                    SelectShape(shape);
                    isMoving = true;
                    moveStartPoint = position;
                    SaveInitialPosition(shape);
                }
                else
                {
                    DeselectShape();
                }
            }
            else
            {
                if (currentTool == ToolMode.Polygon)
                {
                    if (e.ClickCount == 2)
                    {
                        CompletePolygon();
                        e.Handled = true;
                    }
                    else if (e.LeftButton == MouseButtonState.Pressed)
                    {
                        if (!isCreatingPolygon)
                        {
                            StartPolygon(position);
                        }
                        else
                        {
                            AddPolygonPoint(position);
                        }
                        e.Handled = true;
                    }
                }
                else
                {
                    DeselectShape();
                    StartCreatingShape(position);
                }
            }
        }

        private void MainCanvas_MouseMove(object sender, MouseEventArgs e)
        {
            var position = e.GetPosition(mainCanvas);

            if (isMoving && selectedShape != null)
            {
                if (!wasActuallyMoved)
                {
                    SaveInitialPosition(selectedShape);
                    wasActuallyMoved = true;
                }

                MoveShape(position);
            }
            else if (isScaling && selectedShape != null)
            {
                ScaleShape(position);
            }
            else if (isCreating && currentShape != null && currentTool != ToolMode.Polygon)
            {
                UpdateShapeSize(position);
            }
        }

        private void MainCanvas_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (isCreating && currentTool != ToolMode.Polygon)
            {
                FinishCreatingShape();
            }
            else if (isMoving)
            {
                isMoving = false;

                if (wasActuallyMoved && selectedShape != null)
                {
                    SaveFinalPosition("Move", selectedShape);
                }

                wasActuallyMoved = false;
                moveStartPoint = default(Point);
            }
            else if (isScaling)
            {
                isScaling = false;
                if (selectedShape != null)
                {
                    SaveFinalPosition("Scale", selectedShape);
                }
            }
        }

        #endregion

        #region Создание фигур

        private void StartCreatingShape(Point position)
        {
            isCreating = true;
            startPoint = position;

            switch (currentTool)
            {
                case ToolMode.Rectangle:
                    currentShape = new Rectangle
                    {
                        Fill = new SolidColorBrush(fillColor),
                        Stroke = new SolidColorBrush(strokeColor),
                        StrokeThickness = strokeThickness
                    };
                    break;
                case ToolMode.Ellipse:
                    currentShape = new Ellipse
                    {
                        Fill = new SolidColorBrush(fillColor),
                        Stroke = new SolidColorBrush(strokeColor),
                        StrokeThickness = strokeThickness
                    };
                    break;
                case ToolMode.Line:
                    currentShape = new Line
                    {
                        Stroke = new SolidColorBrush(strokeColor),
                        StrokeThickness = strokeThickness,
                        X1 = position.X,
                        Y1 = position.Y,
                        X2 = position.X,
                        Y2 = position.Y
                    };
                    break;
            }

            if (currentShape != null && currentTool != ToolMode.Line)
            {
                Canvas.SetLeft(currentShape, position.X);
                Canvas.SetTop(currentShape, position.Y);
                currentShape.Width = 0;
                currentShape.Height = 0;

                currentShape.MouseDown += Shape_MouseDown;
                mainCanvas.Children.Add(currentShape);
            }
            else if (currentTool == ToolMode.Line)
            {
                currentShape.MouseDown += Shape_MouseDown;
                mainCanvas.Children.Add(currentShape);
            }
        }

        private void UpdateShapeSize(Point position)
        {
            if (currentTool == ToolMode.Line && currentShape is Line line)
            {
                line.X2 = position.X;
                line.Y2 = position.Y;
            }
            else if (currentShape != null)
            {
                double width = position.X - startPoint.X;
                double height = position.Y - startPoint.Y;

                if (width < 0)
                {
                    Canvas.SetLeft(currentShape, position.X);
                    width = Math.Abs(width);
                }
                if (height < 0)
                {
                    Canvas.SetTop(currentShape, position.Y);
                    height = Math.Abs(height);
                }

                currentShape.Width = width;
                currentShape.Height = height;
            }
        }

        private void FinishCreatingShape()
        {
            isCreating = false;
            if (currentShape != null)
            {
                if ((currentShape.Width > 1 || currentShape.Height > 1) || currentTool == ToolMode.Line)
                {
                    SelectShape(currentShape);
                    var fullState = GetFullShapeState(currentShape);
                    SaveAction("Create", currentShape, null, null, null, fullState);
                }
                else
                {
                    mainCanvas.Children.Remove(currentShape);
                }
                currentShape = null;
            }
        }

        private void StartPolygon(Point position)
        {
            isCreatingPolygon = true;
            polygonPoints.Clear();
            polygonPoints.Add(position);

            currentShape = new Polyline
            {
                Stroke = new SolidColorBrush(strokeColor),
                StrokeThickness = strokeThickness,
                StrokeDashArray = new DoubleCollection() { 4, 2 }
            };
            currentShape.SetValue(Polyline.PointsProperty, new PointCollection(polygonPoints));
            mainCanvas.Children.Add(currentShape);
        }

        private void AddPolygonPoint(Point position)
        {
            if (polygonPoints.Count >= 2 && LinesIntersect(polygonPoints, position))
            {
                CompletePolygon();
            }
            else
            {
                polygonPoints.Add(position);
                if (currentShape is Polyline polyline)
                {
                    polyline.Points = new PointCollection(polygonPoints);
                }
            }
        }

        private void CompletePolygon()
        {
            if (mainCanvas == null || polygonPoints.Count < 3) return;

            if (polygonPoints.Count > 2 && polygonPoints[0] != polygonPoints[polygonPoints.Count - 1])
            {
                polygonPoints.Add(polygonPoints[0]);
            }

            var polygon = new Polygon
            {
                Fill = new SolidColorBrush(fillColor),
                Stroke = new SolidColorBrush(strokeColor),
                StrokeThickness = strokeThickness
            };
            polygon.Points = new PointCollection(polygonPoints);

            mainCanvas.Children.Remove(currentShape);
            mainCanvas.Children.Add(polygon);

            polygon.MouseDown += Shape_MouseDown;
            
            SelectShape(polygon);

            var fullState = GetFullShapeState(polygon);
            SaveAction("Create", polygon, null, null, null, fullState);

            isCreatingPolygon = false;
            polygonPoints.Clear();
            currentShape = null;
        }

        private bool LinesIntersect(List<Point> points, Point newPoint)
        {
            if (points.Count < 2) return false;

            Line newLine = new Line
            {
                X1 = points[points.Count - 1].X,
                Y1 = points[points.Count - 1].Y,
                X2 = newPoint.X,
                Y2 = newPoint.Y
            };

            for (int i = 0; i < points.Count - 2; i++)
            {
                Line existingLine = new Line
                {
                    X1 = points[i].X,
                    Y1 = points[i].Y,
                    X2 = points[i + 1].X,
                    Y2 = points[i + 1].Y
                };

                if (DoLinesIntersect(existingLine, newLine))
                {
                    return true;
                }
            }

            return false;
        }

        private bool DoLinesIntersect(Line line1, Line line2)
        {
            double x1 = line1.X1, y1 = line1.Y1;
            double x2 = line1.X2, y2 = line1.Y2;
            double x3 = line2.X1, y3 = line2.Y1;
            double x4 = line2.X2, y4 = line2.Y2;

            double det = (x2 - x1) * (y4 - y3) - (x4 - x3) * (y2 - y1);
            if (det == 0) return false;

            double t = ((x3 - x1) * (y4 - y3) - (x4 - x3) * (y3 - y1)) / det;
            double u = -((x3 - x1) * (y2 - y1) - (x2 - x1) * (y3 - y1)) / det;

            return (t >= 0 && t <= 1 && u >= 0 && u <= 1);
        }

        #endregion

        #region Выделение и управление фигурами

        private void SelectShape(Shape shape)
        {
            DeselectShape();
            selectedShape = shape;
            ShowResizeHandles(shape);
            UpdateColorButtons();
        }

        private void DeselectShape()
        {
            if (selectedShape != null)
            {
                HideResizeHandles();
                selectedShape = null;
                UpdateColorButtons();
            }
        }

        private void Shape_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (currentMode == EditorMode.Editing && sender is Shape shape)
            {
                SelectShape(shape);
                if (e.LeftButton == MouseButtonState.Pressed)
                {
                    isMoving = true;
                    moveStartPoint = e.GetPosition(mainCanvas);
                }

                e.Handled = true;
            }
        }

        #endregion

        #region Масштабирование фигур

        private void ShowResizeHandles(Shape shape)
        {
            HideResizeHandles();

            Rect bounds = GetShapeBounds(shape);

            // Вычисляем динамический размер маркера
            double handleSize = CalculateHandleSize(bounds.Width, bounds.Height);

            for (int i = 0; i < 8; i++)
            {
                var handle = new Rectangle
                {
                    Width = handleSize,
                    Height = handleSize,
                    Fill = Brushes.White,
                    Stroke = Brushes.Black,
                    StrokeThickness = 1,
                    Cursor = GetResizeCursor(i)
                };

                PositionResizeHandle(handle, bounds, i, handleSize);

                handle.MouseDown += ResizeHandle_MouseDown;
                handle.MouseMove += ResizeHandle_MouseMove;
                handle.MouseUp += ResizeHandle_MouseUp;

                resizeHandles.Add(handle);
                mainCanvas.Children.Add(handle);
            }
        }

        private void UpdateResizeHandlesPosition()
        {
            if (selectedShape == null) return;

            Rect bounds = GetShapeBounds(selectedShape);
            double handleSize = CalculateHandleSize(bounds.Width, bounds.Height);

            for (int i = 0; i < resizeHandles.Count; i++)
            {
                var handle = resizeHandles[i];

                // обновление размера маркера
                if (Math.Abs(handle.Width - handleSize) > 0.1)
                {
                    handle.Width = handleSize;
                    handle.Height = handleSize;
                }

                PositionResizeHandle(handle, bounds, i, handleSize);
            }
        }

        private double CalculateHandleSize(double width, double height)
        {
            // размер маркера - процент от размера фигуры с учётом ограничений
            double size = Math.Min(width, height) * HANDLE_SIZE_RATIO;
            return Math.Max(MIN_HANDLE_SIZE, Math.Min(MAX_HANDLE_SIZE, size));
        }

        private void ResizeHandle_MouseMove(object sender, MouseEventArgs e)
        {
            if (isScaling && e.LeftButton == MouseButtonState.Pressed)
            {
                var position = e.GetPosition(mainCanvas);

                ScaleShape(position);
                e.Handled = true;
            }
        }

        private void ResizeHandle_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (isScaling)
            {
                isScaling = false;
                activeHandleIndex = -1; // Сбрасываем активный маркер
                if (selectedShape != null)
                {
                    SaveFinalPosition("Scale", selectedShape);
                    
                    UpdateResizeHandlesPosition(); //обновление маркеров при завершении
                }
                e.Handled = true;
            }
        }

        private void HideResizeHandles()
        {
            foreach (var handle in resizeHandles)
            {
                mainCanvas.Children.Remove(handle); //удаление всех маркеров из визуального дерева
            }
            resizeHandles.Clear();
        }

        private Cursor GetResizeCursor(int handleIndex)
        {
            switch (handleIndex)
            {
                case 0: return Cursors.SizeNWSE;
                case 1: return Cursors.SizeNS;
                case 2: return Cursors.SizeNESW;
                case 3: return Cursors.SizeWE;
                case 4: return Cursors.SizeNWSE;
                case 5: return Cursors.SizeNS;
                case 6: return Cursors.SizeNESW;
                case 7: return Cursors.SizeWE;
                default: return Cursors.Arrow;
            }
        }

        private void PositionResizeHandle(Rectangle handle, Rect bounds, int index, double handleSize)
        {
            double x = 0, y = 0;
            double halfHandle = handleSize / 2;

            switch (index)
            {
                case 0: x = bounds.Left - halfHandle; y = bounds.Top - halfHandle; break;        // Верхний левый
                case 1: x = bounds.Left + bounds.Width / 2 - halfHandle; y = bounds.Top - halfHandle; break; // Верхний средний
                case 2: x = bounds.Right - halfHandle; y = bounds.Top - halfHandle; break;       // Верхний правый
                case 3: x = bounds.Right - halfHandle; y = bounds.Top + bounds.Height / 2 - halfHandle; break; // Правый средний
                case 4: x = bounds.Right - halfHandle; y = bounds.Bottom - halfHandle; break;    // Нижний правый
                case 5: x = bounds.Left + bounds.Width / 2 - halfHandle; y = bounds.Bottom - halfHandle; break; // Нижний средний
                case 6: x = bounds.Left - halfHandle; y = bounds.Bottom - halfHandle; break;     // Нижний левый
                case 7: x = bounds.Left - halfHandle; y = bounds.Top + bounds.Height / 2 - halfHandle; break; // Левый средний
            }

            Canvas.SetLeft(handle, x);
            Canvas.SetTop(handle, y);
        }

        private Rect GetShapeBounds(Shape shape)
        {
            if (shape is Rectangle rect)
            {
                double left = Canvas.GetLeft(rect);
                double top = Canvas.GetTop(rect);
                return new Rect(left, top, rect.Width, rect.Height);
            }
            else if (shape is Ellipse ellipse)
            {
                double left = Canvas.GetLeft(ellipse);
                double top = Canvas.GetTop(ellipse);
                return new Rect(left, top, ellipse.Width, ellipse.Height);
            }
            else if (shape is Line line)
            {
                double minX = Math.Min(line.X1, line.X2);
                double minY = Math.Min(line.Y1, line.Y2);
                double width = Math.Abs(line.X2 - line.X1);
                double height = Math.Abs(line.Y2 - line.Y1);
                return new Rect(minX, minY, width, height);
            }
            else if (shape is Polygon polygon)
            {
                if (polygon.Points.Count == 0) return new Rect();

                double minX = polygon.Points.Min(p => p.X);
                double minY = polygon.Points.Min(p => p.Y);
                double maxX = polygon.Points.Max(p => p.X);
                double maxY = polygon.Points.Max(p => p.Y);
                return new Rect(minX, minY, maxX - minX, maxY - minY);
            }

            return new Rect();
        }

        private void ResizeHandle_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is Rectangle handle && resizeHandles.Contains(handle))
            {
                isScaling = true;
                scaleStartPoint = e.GetPosition(mainCanvas);

                // Сохраняем индекс активного маркера в начале масштабирования
                activeHandleIndex = GetActiveHandleIndex(scaleStartPoint);

                SaveInitialPosition(selectedShape);
                e.Handled = true;
            }
        }

        private int GetActiveHandleIndex(Point position)
        {
            for (int i = 0; i < resizeHandles.Count; i++)
            {
                var handle = resizeHandles[i];
                double left = Canvas.GetLeft(handle);
                double top = Canvas.GetTop(handle);
                Rect handleBounds = new Rect(left, top, handle.Width, handle.Height);

                if (handleBounds.Contains(position))
                {
                    return i;
                }
            }
            return -1;
        }

        private void ScaleShape(Point position)
        {
            if (selectedShape == null || activeHandleIndex == -1) return;

            double deltaX = position.X - scaleStartPoint.X;
            double deltaY = position.Y - scaleStartPoint.Y;

            Rect bounds = GetShapeBounds(selectedShape); // текущие границы фигуры

            // новые размеры и позицию в зависимости от активного маркера
            double newLeft = bounds.Left;
            double newTop = bounds.Top;
            double newWidth = bounds.Width;
            double newHeight = bounds.Height;

            // Используем сохраненный индекс активного маркера
            switch (activeHandleIndex)
            {
                case 0: // Верхний левый
                    newLeft = bounds.Left + deltaX;
                    newTop = bounds.Top + deltaY;
                    newWidth = Math.Max(10, bounds.Width - deltaX);
                    newHeight = Math.Max(10, bounds.Height - deltaY);
                    break;
                case 1: // Верхний средний
                    newTop = bounds.Top + deltaY;
                    newHeight = Math.Max(10, bounds.Height - deltaY);
                    break;
                case 2: // Верхний правый
                    newTop = bounds.Top + deltaY;
                    newWidth = Math.Max(10, bounds.Width + deltaX);
                    newHeight = Math.Max(10, bounds.Height - deltaY);
                    break;
                case 3: // Правый средний
                    newWidth = Math.Max(10, bounds.Width + deltaX);
                    break;
                case 4: // Нижний правый
                    newWidth = Math.Max(10, bounds.Width + deltaX);
                    newHeight = Math.Max(10, bounds.Height + deltaY);
                    break;
                case 5: // Нижний средний
                    newHeight = Math.Max(10, bounds.Height + deltaY);
                    break;
                case 6: // Нижний левый
                    newLeft = bounds.Left + deltaX;
                    newWidth = Math.Max(10, bounds.Width - deltaX);
                    newHeight = Math.Max(10, bounds.Height + deltaY);
                    break;
                case 7: // Левый средний
                    newLeft = bounds.Left + deltaX;
                    newWidth = Math.Max(10, bounds.Width - deltaX);
                    break;
            }

            ApplyScaling(selectedShape, newLeft, newTop, newWidth, newHeight);
            UpdateResizeHandlesPosition();

            scaleStartPoint = position;
        }

        private void ApplyScaling(Shape shape, double left, double top, double width, double height)
        {
            if (shape is Rectangle rect)
            {
                Canvas.SetLeft(rect, left);
                Canvas.SetTop(rect, top);
                rect.Width = width;
                rect.Height = height;
            }
            else if (shape is Ellipse ellipse)
            {
                Canvas.SetLeft(ellipse, left);
                Canvas.SetTop(ellipse, top);
                ellipse.Width = width;
                ellipse.Height = height;
            }
            else if (shape is Line line)
            {
                line.X1 = left;
                line.Y1 = top;
                line.X2 = left + width;
                line.Y2 = top + height;
            }
            else if (shape is Polygon polygon)
            {
                ScalePolygon(polygon, left, top, width, height);
            }
        }

        private void ScalePolygon(Polygon polygon, double newLeft, double newTop, double newWidth, double newHeight)
        {
            if (polygon.Points.Count == 0) return;

            Rect oldBounds = GetShapeBounds(polygon);

            // коэффициенты масштабирования
            double scaleX = newWidth / oldBounds.Width;
            double scaleY = newHeight / oldBounds.Height;

            var newPoints = new PointCollection();
            foreach (Point point in polygon.Points)
            {
                double scaledX = newLeft + (point.X - oldBounds.Left) * scaleX;
                double scaledY = newTop + (point.Y - oldBounds.Top) * scaleY;
                newPoints.Add(new Point(scaledX, scaledY));
            }
            polygon.Points = newPoints;
        }

        private void MoveShape(Point position)
        {
            double deltaX = position.X - moveStartPoint.X;
            double deltaY = position.Y - moveStartPoint.Y;

            if (selectedShape is Line line)
            {
                line.X1 += deltaX;
                line.Y1 += deltaY;
                line.X2 += deltaX;
                line.Y2 += deltaY;
            }
            else if (selectedShape is Polygon polygon)
            {
                var newPoints = new PointCollection();
                foreach (Point point in polygon.Points)
                {
                    newPoints.Add(new Point(point.X + deltaX, point.Y + deltaY));
                }
                polygon.Points = newPoints;
            }
            else
            {
                double currentLeft = Canvas.GetLeft(selectedShape);
                double currentTop = Canvas.GetTop(selectedShape);

                if (!double.IsNaN(currentLeft) && !double.IsNaN(currentTop))
                {
                    Canvas.SetLeft(selectedShape, currentLeft + deltaX);
                    Canvas.SetTop(selectedShape, currentTop + deltaY);
                }
            }

            if (resizeHandles.Count > 0)
            {
                UpdateResizeHandlesPosition(); //обновление позиции маркеров без пересоздания
            }

            moveStartPoint = position;
        }

        #endregion

        #region Система отмены и действий

        private void SaveInitialPosition(Shape shape)
        {
            initialPosition = new ShapePosition();

            if (shape is Line line)
            {
                initialPosition.X = Math.Min(line.X1, line.X2);
                initialPosition.Y = Math.Min(line.Y1, line.Y2);
                initialPosition.Width = Math.Abs(line.X2 - line.X1);
                initialPosition.Height = Math.Abs(line.Y2 - line.Y1);
            }
            else if (shape is Polygon polygon)
            {
                initialPosition.Points = new List<Point>(polygon.Points);
                var bounds = GetShapeBounds(polygon);
                initialPosition.X = bounds.X;
                initialPosition.Y = bounds.Y;
                initialPosition.Width = bounds.Width;
                initialPosition.Height = bounds.Height;
            }
            else
            {
                initialPosition.X = Canvas.GetLeft(shape);
                initialPosition.Y = Canvas.GetTop(shape);
                initialPosition.Width = shape.Width;
                initialPosition.Height = shape.Height;
            }
        }

        private void SaveFinalPosition(string actionType, Shape shape)
        {
            var finalPosition = new ShapePosition();

            if (shape is Line line)
            {
                finalPosition.X = line.X1;
                finalPosition.Y = line.Y1;
                finalPosition.Width = line.X2 - line.X1;
                finalPosition.Height = line.Y2 - line.Y1;
            }
            else if (shape is Polygon polygon)
            {
                finalPosition.Points = new List<Point>(polygon.Points);
            }
            else
            {
                finalPosition.X = Canvas.GetLeft(shape);
                finalPosition.Y = Canvas.GetTop(shape);
                finalPosition.Width = shape.Width;
                finalPosition.Height = shape.Height;
            }

            SaveAction(actionType, shape, initialPosition, finalPosition, "Position");
        }

        private ShapePosition GetFullShapeState(Shape shape)
        {
            var state = new ShapePosition();

            if (shape is Rectangle rect)
            {
                state.X = Canvas.GetLeft(rect);
                state.Y = Canvas.GetTop(rect);
                state.Width = rect.Width;
                state.Height = rect.Height;
            }
            else if (shape is Ellipse ellipse)
            {
                state.X = Canvas.GetLeft(ellipse);
                state.Y = Canvas.GetTop(ellipse);
                state.Width = ellipse.Width;
                state.Height = ellipse.Height;
            }
            else if (shape is Line line)
            {
                state.X = line.X1;
                state.Y = line.Y1;
                state.Width = line.X2 - line.X1;
                state.Height = line.Y2 - line.Y1;
            }
            else if (shape is Polygon polygon)
            {
                state.Points = new List<Point>(polygon.Points);

                if (polygon.Points.Count > 0)
                {
                    state.X = polygon.Points[0].X;
                    state.Y = polygon.Points[0].Y;
                    state.Width = polygon.Points.Max(p => p.X) - polygon.Points.Min(p => p.X);
                    state.Height = polygon.Points.Max(p => p.Y) - polygon.Points.Min(p => p.Y);
                }
            }

            return state;
        }

        private void SaveAction(string type, Shape shape, object oldValue = null, object newValue = null, string property = null, ShapePosition fullState = null)
        {
            if (shape == null)
            {
                Debug.WriteLine("❌ СОХРАНЕНИЕ ОШИБКИ: shape is NULL!");
                return;
            }
            if (type != "Delete" && !mainCanvas.Children.Contains(shape))
            {
                Debug.WriteLine($"❌ СОХРАНЕНИЕ ОШИБКИ: фигура {shape.GetType().Name} не на холсте!");
                return;
            }

            var action = new EditorAction
            {
                Type = type,
                TargetShape = shape,
                OldValue = oldValue,
                NewValue = newValue,
                PropertyName = property,
                FullState = fullState
            };

            if (undoQueue.Count >= MAX_UNDO_STEPS) 
            {
                var removedAction = undoQueue.Dequeue();
            }
            undoQueue.Enqueue(action);

        }

        private void UndoButton_Click(object sender, RoutedEventArgs e)
        {
            if (undoQueue.Count == 0)
            {
                MessageBox.Show("Нет действий для отмены");
                return;
            }

            var lastAction = undoQueue.Last();
            var actionsList = undoQueue.ToList();
            actionsList.RemoveAt(actionsList.Count - 1);
            undoQueue = new Queue<EditorAction>(actionsList);

            if (lastAction.TargetShape == null)
            {
                Debug.WriteLine("❌ ОТМЕНА ОШИБКИ: TargetShape is NULL!");
                return;
            }

            if (lastAction.Type != "Create" && lastAction.Type != "Delete" &&
                !mainCanvas.Children.Contains(lastAction.TargetShape))
            {
                Debug.WriteLine($"❌ ОТМЕНА ОШИБКИ: фигура не на холсте (тип действия: {lastAction.Type})");
                return;
            }

            try
            {
                switch (lastAction.Type)
                {
                    case "Create":
                        if (mainCanvas.Children.Contains(lastAction.TargetShape))
                        {
                            mainCanvas.Children.Remove(lastAction.TargetShape);
                            if (selectedShape == lastAction.TargetShape) DeselectShape();
                        }
                        break;

                    case "Delete":
                        if (!mainCanvas.Children.Contains(lastAction.TargetShape))
                        {
                            if (lastAction.FullState != null)
                            {
                                RestoreFullShape(lastAction.TargetShape, lastAction.FullState);
                            }
                            mainCanvas.Children.Add(lastAction.TargetShape);
                            SelectShape(lastAction.TargetShape);
                        }
                        break;

                    case "Move":
                    case "Scale":
                        if (lastAction.OldValue is ShapePosition oldPos)
                        {
                            RestorePosition(lastAction.TargetShape, oldPos);
                            if (selectedShape == lastAction.TargetShape)
                            {
                                ShowResizeHandles(lastAction.TargetShape);
                            }
                        }
                        break;

                    case "ModifyColor":
                        if (lastAction.OldValue is Color oldColor)
                        {
                            lastAction.TargetShape.Fill = new SolidColorBrush(oldColor);
                            UpdateColorButtons();
                        }
                        break;

                    case "ModifyStroke":
                        if (lastAction.OldValue is Color oldStrokeColor)
                        {
                            lastAction.TargetShape.Stroke = new SolidColorBrush(oldStrokeColor);
                            UpdateColorButtons();
                        }
                        break;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"❌ ОШИБКА ПРИ ОТМЕНЕ: {ex.Message}");
            }

        }

        private void RestorePosition(Shape shape, ShapePosition position)
        {
            if (shape is Line line)
            {
                line.X1 = position.X;
                line.Y1 = position.Y;
                line.X2 = position.X + position.Width;
                line.Y2 = position.Y + position.Height;
            }
            else if (shape is Polygon polygon && position.Points != null)
            {
                polygon.Points = new PointCollection(position.Points);
            }
            else
            {
                Canvas.SetLeft(shape, position.X);
                Canvas.SetTop(shape, position.Y);
                shape.Width = position.Width;
                shape.Height = position.Height;
            }
        }
        private void RestoreFullShape(Shape shape, ShapePosition state)
        {
            if (shape is Rectangle rect)
            {
                Canvas.SetLeft(rect, state.X);
                Canvas.SetTop(rect, state.Y);
                rect.Width = state.Width;
                rect.Height = state.Height;
            }
            else if (shape is Ellipse ellipse)
            {
                Canvas.SetLeft(ellipse, state.X);
                Canvas.SetTop(ellipse, state.Y);
                ellipse.Width = state.Width;
                ellipse.Height = state.Height;
            }
            else if (shape is Line line)
            {
                line.X1 = state.X;
                line.Y1 = state.Y;
                line.X2 = state.X + state.Width;
                line.Y2 = state.Y + state.Height;
            }
            else if (shape is Polygon polygon && state.Points != null)
            {
                polygon.Points = new PointCollection(state.Points);
            }
        }

        #endregion

        #region Обработчики кнопок

        private void FillColorButton_Click(object sender, RoutedEventArgs e)
        {
            if (fillColorButton == null) return;

            var contextMenu = new ContextMenu();

            foreach (var colorInfo in fillColors)
            {
                var stackPanel = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Background = Brushes.White
                };

                var colorRect = new Border
                {
                    Width = 20,
                    Height = 15,
                    Background = new SolidColorBrush(colorInfo.Color),
                    Margin = new Thickness(0, 0, 8, 0),
                    BorderBrush = Brushes.Black,
                    BorderThickness = new Thickness(1)
                };

                var textBlock = new TextBlock
                {
                    Text = colorInfo.Name,
                    Foreground = Brushes.Black,
                    VerticalAlignment = VerticalAlignment.Center
                };

                stackPanel.Children.Add(colorRect);
                stackPanel.Children.Add(textBlock);

                var menuItem = new MenuItem
                {
                    Header = stackPanel,
                    Background = Brushes.White
                };

                menuItem.Click += (s, args) =>
                {
                    if (currentMode == EditorMode.Editing && selectedShape != null)
                    {
                        if (selectedShape is Line)
                        {
                            return;
                        }

                        if (!mainCanvas.Children.Contains(selectedShape))
                        {
                            return;
                        }

                        var oldColor = ((SolidColorBrush)selectedShape.Fill).Color;
                        SaveAction("ModifyColor", selectedShape, oldColor, colorInfo.Color, "FillColor");
                        selectedShape.Fill = new SolidColorBrush(colorInfo.Color);

                    }
                    else if (currentMode == EditorMode.Creating)
                    {
                        fillColor = colorInfo.Color;
                        UpdateColorButtons();
                    }
                };
                contextMenu.Items.Add(menuItem);
            }

            contextMenu.PlacementTarget = fillColorButton;
            contextMenu.IsOpen = true;
        }

        private void StrokeColorButton_Click(object sender, RoutedEventArgs e)
        {
            if (strokeColorButton == null) return;

            var contextMenu = new ContextMenu();

            foreach (var colorInfo in strokeColors)
            {
                var stackPanel = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Background = Brushes.White
                };

                var colorRect = new Border
                {
                    Width = 20,
                    Height = 15,
                    Background = new SolidColorBrush(colorInfo.Color),
                    Margin = new Thickness(0, 0, 8, 0),
                    BorderBrush = Brushes.Black,
                    BorderThickness = new Thickness(1)
                };

                var textBlock = new TextBlock
                {
                    Text = colorInfo.Name,
                    Foreground = Brushes.Black,
                    VerticalAlignment = VerticalAlignment.Center
                };

                stackPanel.Children.Add(colorRect);
                stackPanel.Children.Add(textBlock);

                var menuItem = new MenuItem
                {
                    Header = stackPanel,
                    Background = Brushes.White
                };

                menuItem.Click += (s, args) =>
                {
                    if (currentMode == EditorMode.Editing && selectedShape != null)
                    {
                        if (!mainCanvas.Children.Contains(selectedShape))
                        {
                            MessageBox.Show("Ошибка: выбранная фигура не найдена");
                            return;
                        }

                        var oldColor = ((SolidColorBrush)selectedShape.Stroke).Color;
                        SaveAction("ModifyStroke", selectedShape, oldColor, colorInfo.Color, "StrokeColor");
                        selectedShape.Stroke = new SolidColorBrush(colorInfo.Color);
                        strokeColor = colorInfo.Color;
                        UpdateColorButtons();

                    }
                    else if (currentMode == EditorMode.Creating)
                    {
                        strokeColor = colorInfo.Color;
                        UpdateColorButtons();
                    }
                };
                contextMenu.Items.Add(menuItem);
            }

            contextMenu.PlacementTarget = strokeColorButton;
            contextMenu.IsOpen = true;
        }

        private void DeleteButton_Click(object sender, RoutedEventArgs e)
        {
            if (selectedShape != null && mainCanvas != null)
            {
                var fullState = GetFullShapeState(selectedShape);
                SaveAction("Delete", selectedShape, null, null, null, fullState);
                mainCanvas.Children.Remove(selectedShape);
                DeselectShape();
            }
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            SaveToSvg();
        }

        #endregion

        #region Сохранение в SVG

        private void SaveToSvg()
        {
            if (mainCanvas == null) return;

            var saveDialog = new SaveFileDialog
            {
                Filter = "SVG files (*.svg)|*.svg|All files (*.*)|*.*",
                DefaultExt = "svg"
            };

            if (saveDialog.ShowDialog() == true)
            {
                try
                {
                    using (var writer = new StreamWriter(saveDialog.FileName))
                    {
                        writer.WriteLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
                        writer.WriteLine($"<svg width=\"{mainCanvas.Width}\" height=\"{mainCanvas.Height}\" xmlns=\"http://www.w3.org/2000/svg\">");

                        writer.WriteLine("<rect width=\"100%\" height=\"100%\" fill=\"white\"/>");

                        int savedCount = 0;
                        foreach (var element in mainCanvas.Children)
                        {
                            if (element is Shape shape)
                            {
                                string svg = ShapeToSvg(shape);
                                if (!string.IsNullOrEmpty(svg))
                                {
                                    writer.WriteLine(svg);
                                    savedCount++;
                                }
                            }
                        }

                        writer.WriteLine("</svg>");
                    }

                    MessageBox.Show("Файл успешно сохранен!");
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Ошибка сохранения: {ex.Message}");
                }
            }
        }

        private string ShapeToSvg(Shape shape)
        {
            try
            {
                if (shape is Rectangle rect)
                {
                    double left = Canvas.GetLeft(rect);
                    double top = Canvas.GetTop(rect);

                    if (double.IsNaN(left)) left = 0;
                    if (double.IsNaN(top)) top = 0;
                    if (rect.Width <= 0 || rect.Height <= 0) return "";

                    Color fillColor = ((SolidColorBrush)rect.Fill).Color;
                    Color strokeColor = ((SolidColorBrush)rect.Stroke).Color;

                    return $"<rect x=\"{left:F0}\" y=\"{top:F0}\" width=\"{rect.Width:F0}\" height=\"{rect.Height:F0}\" " +
                           $"fill=\"{ColorToHex(fillColor)}\" " +
                           $"stroke=\"{ColorToHex(strokeColor)}\" " +
                           $"stroke-width=\"{rect.StrokeThickness:F0}\"/>";
                }
                else if (shape is Ellipse ellipse)
                {
                    double left = Canvas.GetLeft(ellipse);
                    double top = Canvas.GetTop(ellipse);

                    if (double.IsNaN(left)) left = 0;
                    if (double.IsNaN(top)) top = 0;
                    if (ellipse.Width <= 0 || ellipse.Height <= 0) return "";

                    double cx = left + ellipse.Width / 2;
                    double cy = top + ellipse.Height / 2;
                    double rx = ellipse.Width / 2;
                    double ry = ellipse.Height / 2;

                    Color fillColor = ((SolidColorBrush)ellipse.Fill).Color;
                    Color strokeColor = ((SolidColorBrush)ellipse.Stroke).Color;

                    return $"<ellipse cx=\"{cx:F0}\" cy=\"{cy:F0}\" rx=\"{rx:F0}\" ry=\"{ry:F0}\" " +
                           $"fill=\"{ColorToHex(fillColor)}\" " +
                           $"stroke=\"{ColorToHex(strokeColor)}\" " +
                           $"stroke-width=\"{ellipse.StrokeThickness:F0}\"/>";
                }
                else if (shape is Line line)
                {
                    Color strokeColor = ((SolidColorBrush)line.Stroke).Color;

                    return $"<line x1=\"{line.X1:F0}\" y1=\"{line.Y1:F0}\" x2=\"{line.X2:F0}\" y2=\"{line.Y2:F0}\" " +
                           $"stroke=\"{ColorToHex(strokeColor)}\" " +
                           $"stroke-width=\"{line.StrokeThickness:F0}\"/>";
                }
                else if (shape is Polygon polygon)
                {
                    if (polygon.Points.Count < 3) return "";

                    string points = "";
                    foreach (Point point in polygon.Points)
                    {
                        points += $"{point.X:F0},{point.Y:F0} ";
                    }

                    Color fillColor = ((SolidColorBrush)polygon.Fill).Color;
                    Color strokeColor = ((SolidColorBrush)polygon.Stroke).Color;

                    return $"<polygon points=\"{points.Trim()}\" " +
                           $"fill=\"{ColorToHex(fillColor)}\" " +
                           $"stroke=\"{ColorToHex(strokeColor)}\" " +
                           $"stroke-width=\"{polygon.StrokeThickness:F0}\"/>";
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"❌ Ошибка конвертации фигуры: {ex.Message}");
            }

            return "";
        }

        private string ColorToHex(Color color)
        {
            return $"#{color.R:X2}{color.G:X2}{color.B:X2}";
        }

        #endregion
    }
}