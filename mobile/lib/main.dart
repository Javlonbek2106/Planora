import 'package:flutter/material.dart';

import 'demo/demo_plans.dart';
import 'state/plan_controller.dart';
import 'ui/home_page.dart';

void main() {
  // Development convenience: open the app with ?demo=apartment or ?demo=lattice to start from a demo plan.
  final demo = Uri.base.queryParameters['demo'];
  final controller = PlanController(
    model: switch (demo) {
      'apartment' => DemoPlans.apartment(),
      'lattice' => DemoPlans.lattice(),
      _ => null,
    },
  );
  runApp(BuildingDraftApp(controller: controller));
}

class BuildingDraftApp extends StatelessWidget {
  const BuildingDraftApp({super.key, this.controller});

  final PlanController? controller;

  @override
  Widget build(BuildContext context) {
    return MaterialApp(
      title: 'Building Draft',
      debugShowCheckedModeBanner: false,
      theme: ThemeData(colorSchemeSeed: const Color(0xFF2F5D8A), useMaterial3: true),
      darkTheme: ThemeData(colorSchemeSeed: const Color(0xFF2F5D8A), useMaterial3: true, brightness: Brightness.dark),
      home: HomePage(controller: controller),
    );
  }
}
