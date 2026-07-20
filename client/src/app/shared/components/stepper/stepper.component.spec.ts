import { Component } from '@angular/core';
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

  it('should render a disabled nav button for each step showing the step label', () => {
    const hostFixture = TestBed.createComponent(StepperHostComponent);
    hostFixture.detectChanges();
    const buttons = hostFixture.debugElement.queryAll(By.css('button.nav-link'));
    expect(buttons.length).toBe(2);
    expect(buttons[0].nativeElement.textContent.trim()).toBe('One');
    expect(buttons[1].nativeElement.textContent.trim()).toBe('Two');
    // The production template hard-codes [disabled]="true" on every nav button.
    expect(buttons[0].nativeElement.disabled).toBe(true);
    expect(buttons[1].nativeElement.disabled).toBe(true);
  });

  it('should activate only the selected step button and project that step content', () => {
    const hostFixture = TestBed.createComponent(StepperHostComponent);
    hostFixture.detectChanges();
    const component: StepperComponent =
      hostFixture.debugElement.query(By.directive(StepperComponent)).componentInstance;
    const buttons = hostFixture.debugElement.queryAll(By.css('button.nav-link'));
    const content = hostFixture.debugElement.query(By.css('.container > div')).nativeElement;

    // Initial render: the first step is selected and its content is projected.
    expect(buttons[0].nativeElement.classList).toContain('active');
    expect(buttons[1].nativeElement.classList).not.toContain('active');
    expect(content.textContent).toContain('Step one content');

    // Navigate to the second step through the production OnClick API.
    component.OnClick(1);
    hostFixture.detectChanges();
    expect(component.selectedIndex).toBe(1);
    expect(buttons[1].nativeElement.classList).toContain('active');
    expect(buttons[0].nativeElement.classList).not.toContain('active');
    expect(content.textContent).toContain('Step two content');
    expect(content.textContent).not.toContain('Step one content');

    // Navigate back to the first step.
    component.OnClick(0);
    hostFixture.detectChanges();
    expect(component.selectedIndex).toBe(0);
    expect(buttons[0].nativeElement.classList).toContain('active');
    expect(buttons[1].nativeElement.classList).not.toContain('active');
    expect(content.textContent).toContain('Step one content');
  });

  it('should wire each nav button click to OnClick(index) while the disabled attribute blocks native clicks', () => {
    const hostFixture = TestBed.createComponent(StepperHostComponent);
    hostFixture.detectChanges();
    const component: StepperComponent =
      hostFixture.debugElement.query(By.directive(StepperComponent)).componentInstance;
    const buttons = hostFixture.debugElement.queryAll(By.css('button.nav-link'));
    const onClickSpy = spyOn(component, 'OnClick').and.callThrough();

    // A disabled button must not dispatch a native click through to the handler.
    buttons[1].nativeElement.click();
    expect(onClickSpy).not.toHaveBeenCalled();
    expect(component.selectedIndex).toBe(0);

    // The template binding (click)="OnClick(i)" forwards the button's own index.
    buttons[1].triggerEventHandler('click', {});
    hostFixture.detectChanges();
    expect(onClickSpy).toHaveBeenCalledWith(1);
    expect(component.selectedIndex).toBe(1);
  });
});
