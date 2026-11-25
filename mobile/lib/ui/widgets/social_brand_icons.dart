import 'package:flutter/material.dart';

class GoogleBrandIcon extends StatelessWidget {
  const GoogleBrandIcon({super.key, this.size = 20});

  final double size;

  @override
  Widget build(BuildContext context) {
    return CustomPaint(
      size: Size.square(size),
      painter: const _GoogleBrandPainter(),
    );
  }
}

class QqBrandIcon extends StatelessWidget {
  const QqBrandIcon({super.key, this.size = 20});

  final double size;

  @override
  Widget build(BuildContext context) {
    return CustomPaint(
      size: Size.square(size),
      painter: const _QqBrandPainter(),
    );
  }
}

class WechatBrandIcon extends StatelessWidget {
  const WechatBrandIcon({super.key, this.size = 20});

  final double size;

  @override
  Widget build(BuildContext context) {
    return CustomPaint(
      size: Size.square(size),
      painter: const _WechatBrandPainter(),
    );
  }
}

class _GoogleBrandPainter extends CustomPainter {
  const _GoogleBrandPainter();

  @override
  void paint(Canvas canvas, Size size) {
    canvas.save();
    canvas.scale(size.width / 24.0, size.height / 24.0);

    final blue = Path()
      ..moveTo(23.745, 12.27)
      ..cubicTo(23.745, 11.48, 23.675, 10.73, 23.555, 10.0)
      ..lineTo(12.255, 10.0)
      ..lineTo(12.255, 14.51)
      ..lineTo(18.725, 14.51)
      ..cubicTo(18.435, 15.99, 17.585, 17.24, 16.325, 18.09)
      ..lineTo(16.325, 21.09)
      ..lineTo(20.185, 21.09)
      ..cubicTo(22.445, 19.0, 23.745, 15.92, 23.745, 12.27)
      ..close();
    canvas.drawPath(blue, Paint()..color = const Color(0xFF4285F4));

    final green = Path()
      ..moveTo(12.255, 24.0)
      ..cubicTo(15.495, 24.0, 18.205, 22.92, 20.185, 21.09)
      ..lineTo(16.325, 18.09)
      ..cubicTo(15.245, 18.81, 13.875, 19.25, 12.255, 19.25)
      ..cubicTo(9.125, 19.25, 6.475, 17.14, 5.525, 14.29)
      ..lineTo(1.545, 14.29)
      ..lineTo(1.545, 17.38)
      ..cubicTo(3.515, 21.3, 7.565, 24.0, 12.255, 24.0)
      ..close();
    canvas.drawPath(green, Paint()..color = const Color(0xFF34A853));

    final yellow = Path()
      ..moveTo(5.525, 14.29)
      ..cubicTo(5.275, 13.57, 5.145, 12.8, 5.145, 12.0)
      ..cubicTo(5.145, 11.2, 5.285, 10.43, 5.525, 9.71)
      ..lineTo(5.525, 6.62)
      ..lineTo(1.545, 6.62)
      ..arcToPoint(
        const Offset(1.545, 17.38),
        radius: const Radius.circular(11.86),
        largeArc: false,
        clockwise: false,
      )
      ..lineTo(5.525, 14.29)
      ..close();
    canvas.drawPath(yellow, Paint()..color = const Color(0xFFFBBC05));

    final red = Path()
      ..moveTo(12.255, 4.75)
      ..cubicTo(14.025, 4.75, 15.605, 5.36, 16.855, 6.55)
      ..lineTo(20.275, 3.13)
      ..cubicTo(18.205, 1.19, 15.495, 0.0, 12.255, 0.0)
      ..cubicTo(7.565, 0.0, 3.515, 2.7, 1.545, 6.62)
      ..lineTo(5.525, 9.71)
      ..cubicTo(6.475, 6.86, 9.125, 4.75, 12.255, 4.75)
      ..close();
    canvas.drawPath(red, Paint()..color = const Color(0xFFEA4335));

    canvas.restore();
  }

  @override
  bool shouldRepaint(covariant _GoogleBrandPainter oldDelegate) => false;
}

class _QqBrandPainter extends CustomPainter {
  const _QqBrandPainter();

  @override
  void paint(Canvas canvas, Size size) {
    canvas.save();
    canvas.scale(size.width / 24.0, size.height / 24.0);

    final path = Path()
      ..moveTo(21.395, 15.035)
      ..arcToPoint(
        const Offset(20.592, 12.771),
        radius: const Radius.circular(39.548),
        largeArc: false,
        clockwise: false,
      )
      ..lineTo(19.513, 10.076)
      ..cubicTo(19.514, 10.044, 19.527, 9.514, 19.527, 9.24)
      ..cubicTo(19.526, 4.632, 17.351, 0.0, 12.0, 0.0)
      ..cubicTo(6.649, 0.0, 4.474, 4.632, 4.474, 9.241)
      ..cubicTo(4.474, 9.515, 4.487, 10.045, 4.488, 10.077)
      ..lineTo(3.408, 12.772)
      ..arcToPoint(
        const Offset(2.606, 15.036),
        radius: const Radius.circular(38.97),
        largeArc: false,
        clockwise: false,
      )
      ..cubicTo(1.585, 18.319, 1.916, 19.679, 2.168, 19.709)
      ..cubicTo(2.708, 19.774, 4.271, 17.237, 4.271, 17.237)
      ..cubicTo(4.271, 18.706, 5.027, 20.624, 6.665, 22.008)
      ..cubicTo(6.053, 22.196, 5.302, 22.487, 4.82, 22.843)
      ..cubicTo(4.386, 23.163, 4.441, 23.489, 4.519, 23.621)
      ..cubicTo(4.862, 24.199, 10.402, 23.99, 12.001, 23.81)
      ..cubicTo(13.601, 23.99, 19.141, 24.199, 19.484, 23.621)
      ..cubicTo(19.562, 23.489, 19.616, 23.163, 19.183, 22.843)
      ..cubicTo(18.7, 22.487, 17.95, 22.197, 17.337, 22.007)
      ..cubicTo(18.974, 20.623, 19.73, 18.705, 19.73, 17.236)
      ..cubicTo(19.73, 17.236, 21.293, 19.773, 21.833, 19.708)
      ..cubicTo(22.084, 19.678, 22.414, 18.318, 21.393, 15.036)
      ..close();
    canvas.drawPath(path, Paint()..color = const Color(0xFF12B7F5));

    canvas.restore();
  }

  @override
  bool shouldRepaint(covariant _QqBrandPainter oldDelegate) => false;
}

class _WechatBrandPainter extends CustomPainter {
  const _WechatBrandPainter();

  @override
  void paint(Canvas canvas, Size size) {
    canvas.save();
    canvas.scale(size.width / 24.0, size.height / 24.0);

    final path = Path()
      ..fillType = PathFillType.evenOdd
      ..moveTo(8.691, 2.188)
      ..cubicTo(3.891, 2.188, 0.0, 5.476, 0.0, 9.53)
      ..cubicTo(0.0, 11.742, 1.17, 13.733, 3.002, 15.08)
      ..arcToPoint(
        const Offset(3.215, 15.745),
        radius: const Radius.circular(0.59),
        largeArc: false,
        clockwise: true,
      )
      ..lineTo(2.825, 17.225)
      ..cubicTo(2.806, 17.295, 2.777, 17.366, 2.777, 17.438)
      ..cubicTo(2.777, 17.601, 2.907, 17.733, 3.067, 17.733)
      ..arcToPoint(
        const Offset(3.234, 17.679),
        radius: const Radius.circular(0.326),
        largeArc: false,
        clockwise: false,
      )
      ..lineTo(5.137, 16.565)
      ..arcToPoint(
        const Offset(5.854, 16.467),
        radius: const Radius.circular(0.864),
        largeArc: false,
        clockwise: true,
      )
      ..arcToPoint(
        const Offset(8.691, 16.87),
        radius: const Radius.circular(10.16),
        largeArc: false,
        clockwise: false,
      )
      ..cubicTo(8.967, 16.87, 9.234, 16.843, 9.502, 16.82)
      ..cubicTo(8.645, 14.242, 9.659, 11.848, 11.434, 10.374)
      ..cubicTo(13.137, 8.959, 15.316, 8.394, 17.287, 8.536)
      ..cubicTo(16.711, 4.953, 13.091, 2.188, 8.691, 2.188)
      ..close()
      ..addOval(Rect.fromCenter(center: const Offset(5.785, 7.171), width: 2.324, height: 2.358))
      ..addOval(Rect.fromCenter(center: const Offset(11.598, 7.171), width: 2.324, height: 2.358))
      ..moveTo(16.938, 8.858)
      ..cubicTo(15.141, 8.806, 13.192, 9.37, 11.658, 10.644)
      ..cubicTo(9.938, 12.072, 8.971, 14.364, 9.878, 16.864)
      ..cubicTo(10.82, 19.317, 13.544, 21.093, 16.762, 21.093)
      ..cubicTo(17.588, 21.093, 18.384, 20.973, 19.123, 20.757)
      ..arcToPoint(
        const Offset(19.721, 20.839),
        radius: const Radius.circular(0.722),
        largeArc: false,
        clockwise: true,
      )
      ..lineTo(21.305, 21.765)
      ..arcToPoint(
        const Offset(21.445, 21.812),
        radius: const Radius.circular(0.272),
        largeArc: false,
        clockwise: false,
      )
      ..cubicTo(21.579, 21.812, 21.685, 21.701, 21.685, 21.565)
      ..cubicTo(21.685, 21.505, 21.662, 21.445, 21.647, 21.388)
      ..lineTo(21.32, 20.155)
      ..arcToPoint(
        const Offset(21.297, 19.999),
        radius: const Radius.circular(0.582),
        largeArc: false,
        clockwise: true,
      )
      ..arcToPoint(
        const Offset(21.498, 19.601),
        radius: const Radius.circular(0.49),
        largeArc: false,
        clockwise: true,
      )
      ..cubicTo(23.024, 18.48, 24.0, 16.82, 24.0, 14.98)
      ..cubicTo(24.0, 11.77, 21.069, 9.143, 17.344, 8.892)
      ..lineTo(17.344, 8.89)
      ..cubicTo(17.209, 8.88, 17.074, 8.863, 16.937, 8.858)
      ..close()
      ..addOval(Rect.fromCenter(center: const Offset(14.408, 13.114), width: 1.938, height: 1.965))
      ..addOval(Rect.fromCenter(center: const Offset(19.252, 13.114), width: 1.938, height: 1.965));

    canvas.drawPath(path, Paint()..color = const Color(0xFF07C160));
    canvas.restore();
  }

  @override
  bool shouldRepaint(covariant _WechatBrandPainter oldDelegate) => false;
}
