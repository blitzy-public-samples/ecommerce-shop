import { Component, NO_ERRORS_SCHEMA } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { FormControl, ReactiveFormsModule, Validators } from '@angular/forms';
import { By } from '@angular/platform-browser';
import { TextInputComponent } from './text-input.component';

@Component({
  template: '<app-text-input [formControl]="control" label="Email"></app-text-input>'
})
class TextInputHostComponent {
  control = new FormControl('');
}

describe('TextInputComponent', () => {
  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [ReactiveFormsModule],
      declarations: [TextInputComponent, TextInputHostComponent],
      schemas: [NO_ERRORS_SCHEMA],
    }).compileComponents();
  });

  function createComponent(control?: FormControl): TextInputComponent {
    const hostFixture: ComponentFixture<TextInputHostComponent> =
      TestBed.createComponent(TextInputHostComponent);
    if (control) {
      hostFixture.componentInstance.control = control;
    }
    hostFixture.detectChanges();
    return hostFixture.debugElement.query(By.directive(TextInputComponent)).componentInstance;
  }

  it('should create', () => {
    const component = createComponent();
    expect(component).toBeTruthy();
  });

  it('should set the native input value when writeValue receives a value', () => {
    const component = createComponent();
    component.writeValue('abc');
    expect(component.input.nativeElement.value).toBe('abc');
  });

  it('should set the native input value to an empty string when writeValue receives null', () => {
    const component = createComponent();
    component.writeValue(null);
    expect(component.input.nativeElement.value).toBe('');
  });

  it('should invoke the registered onChange callback', () => {
    const component = createComponent();
    const spy = jasmine.createSpy('onChange');
    component.registerOnChange(spy);
    component.onChange('x');
    expect(spy).toHaveBeenCalledWith('x');
  });

  it('should invoke the registered onTouched callback', () => {
    const component = createComponent();
    const spy = jasmine.createSpy('onTouched');
    component.registerOnTouched(spy);
    component.onTouched();
    expect(spy).toHaveBeenCalled();
  });

  it('should preserve existing validators after ngOnInit', () => {
    const component = createComponent(new FormControl('', Validators.required));
    expect(component.controlDir.control.validator).toBeTruthy();
  });
});
