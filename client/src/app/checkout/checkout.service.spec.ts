import { TestBed } from '@angular/core/testing';
import { HttpClientTestingModule, HttpTestingController } from '@angular/common/http/testing';

import { CheckoutService } from './checkout.service';
import { environment } from '../../environments/environment';
import { IDeliveryMethod } from '../shared/models/deliveryMethod';
import { IOrderToCreate } from '../shared/models/order';

describe('CheckoutService', () => {
  let service: CheckoutService;
  let httpMock: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [HttpClientTestingModule],
      providers: [CheckoutService]
    });

    service = TestBed.inject(CheckoutService);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    // Guarantees there are no outstanding or unexpected HTTP requests.
    httpMock.verify();
  });

  it('should be created', () => {
    expect(service).toBeTruthy();
  });

  describe('getDeliveryMethods', () => {
    it('should GET the delivery methods and sort them by price descending', () => {
      // Arrange - prices intentionally out of order ([5, 2, 10]) so the sort is observable.
      const unsortedDeliveryMethods: IDeliveryMethod[] = [
        { id: 2, shortName: 'UPS2', deliveryTime: '2-5 Days', description: 'Get it within 5 days', price: 5 },
        { id: 3, shortName: 'UPS3', deliveryTime: '5-10 Days', description: 'Slower but cheap', price: 2 },
        { id: 1, shortName: 'UPS1', deliveryTime: '1-2 Days', description: 'Fastest delivery time', price: 10 }
      ];

      // Act
      let actual: IDeliveryMethod[];
      service.getDeliveryMethods().subscribe(deliveryMethods => {
        actual = deliveryMethods as IDeliveryMethod[];
      });

      const req = httpMock.expectOne(environment.apiUrl + 'orders/deliveryMethods');
      expect(req.request.method).toBe('GET');
      req.flush(unsortedDeliveryMethods);

      // Assert - emitted array must be sorted by price descending ([10, 5, 2]).
      expect(actual).toBeTruthy();
      expect(actual.length).toBe(3);
      expect(actual[0].price).toBe(10);
      expect(actual[1].price).toBe(5);
      expect(actual[2].price).toBe(2);

      // Extra rigor: verify the ordering invariant across every adjacent pair.
      for (let i = 0; i < actual.length - 1; i++) {
        expect(actual[i].price).toBeGreaterThanOrEqual(actual[i + 1].price);
      }
    });
  });

  describe('createOrder', () => {
    it('should POST the order to the orders endpoint with the order as the body', () => {
      // Arrange - a fully-populated order with a 7-field shipping address.
      const orderToCreate: IOrderToCreate = {
        basketId: 'basket_id_1',
        deliveryMethodId: 1,
        shipToAddress: {
          id: 1,
          firstName: 'Bob',
          lastName: 'Smith',
          street: '10 The Street',
          city: 'New York',
          state: 'NY',
          zipCode: '90210'
        }
      };
      const mockCreatedOrder = { id: 1, ...orderToCreate };

      // Act
      let actual: any;
      service.createOrder(orderToCreate).subscribe(response => {
        actual = response;
      });

      const req = httpMock.expectOne(environment.apiUrl + 'orders');
      expect(req.request.method).toBe('POST');
      expect(req.request.body).toEqual(orderToCreate);
      req.flush(mockCreatedOrder);

      // Assert - the created order is emitted back to the caller unchanged.
      expect(actual).toEqual(mockCreatedOrder);
    });
  });
});
