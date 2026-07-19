import { Component, NO_ERRORS_SCHEMA } from '@angular/core';
import { CommonModule } from '@angular/common';
import { TestBed } from '@angular/core/testing';
import { CdkStepperModule } from '@angular/cdk/stepper';
import { By } from '@angular/platform-browser';
import { StepperComponent } from './stepper.component';

@Component({
  template: `
    <app-stepper [linearModeSelected]="linearModeSelected">
      <cdk-step label="One"><p>Step one content</p></cdk-step>
      <cdk-step label="Two"><p>Step two content</p></cdk-step>
    </app-stepper>
  `
})
class StepperHostComponent {
  linearModeSelected = false;
}

describe('StepperComponent', () => {
  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [CommonModule, CdkStepperModule],
      declarations: [StepperComponent, StepperHostComponent],
      schemas: [NO_ERRORS_SCHEMA],
    }).compileComponents();
  });

  it('should create', () => {
    const hostFixture = TestBed.createComponent(StepperHostComponent);
    hostFixture.detectChanges();
    const component: StepperComponent =
      hostFixture.debugElement.query(By.directive(StepperComponent)).componentInstance;
    expect(component).toBeTruthy();
  });

  it('should copy linearModeSelected into the inherited linear property on ngOnInit', () => {
    const hostFixture = TestBed.createComponent(StepperHostComponent);
    hostFixture.componentInstance.linearModeSelected = true;
    hostFixture.detectChanges();
    const component: StepperComponent =
      hostFixture.debugElement.query(By.directive(StepperComponent)).componentInstance;
    expect(component.linear).toBe(true);
  });

  it('should update linear from false to true when ngOnInit runs', () => {
    const hostFixture = TestBed.createComponent(StepperHostComponent);
    hostFixture.detectChanges();
    const component: StepperComponent =
      hostFixture.debugElement.query(By.directive(StepperComponent)).componentInstance;
    expect(component.linear).toBe(false);
    component.linearModeSelected = true;
    component.ngOnInit();
    expect(component.linear).toBe(true);
  });

  it('should set selectedIndex when OnClick is called in non-linear mode', () => {
    const hostFixture = TestBed.createComponent(StepperHostComponent);
    hostFixture.detectChanges();
    const component: StepperComponent =
      hostFixture.debugElement.query(By.directive(StepperComponent)).componentInstance;
    component.OnClick(1);
    expect(component.selectedIndex).toBe(1);
    component.OnClick(0);
    expect(component.selectedIndex).toBe(0);
  });
});
