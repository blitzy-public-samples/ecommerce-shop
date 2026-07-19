import { TestBed } from '@angular/core/testing';
import { HttpClientTestingModule, HttpTestingController } from '@angular/common/http/testing';

import { OrdersService } from './orders.service';
import { environment } from '../../environments/environment';
import { IOrder } from '../shared/models/order';

describe('OrdersService', () => {
  let service: OrdersService;
  let httpMock: HttpTestingController;
  const apiUrl = environment.apiUrl;

  // Complete IOrder fixtures: every required field is provided because
  // object-literal assignability is enforced even though `strict` is off.
  const mockOrders: IOrder[] = [
    {
      id: 1,
      buyerEmail: 'bob@test.com',
      orderDate: new Date('2021-01-01T00:00:00'),
      shipToAddress: {
        id: 1,
        firstName: 'Bob',
        lastName: 'Smith',
        street: '10 The Street',
        city: 'New York',
        state: 'NY',
        zipCode: '90210'
      },
      deliveryMethod: 'UPS1',
      shippingPrice: 5,
      orderItems: [],
      subtotal: 100,
      total: 105,
      status: 'Pending'
    },
    {
      id: 2,
      buyerEmail: 'bob@test.com',
      orderDate: new Date('2021-02-01T00:00:00'),
      shipToAddress: {
        id: 1,
        firstName: 'Bob',
        lastName: 'Smith',
        street: '10 The Street',
        city: 'New York',
        state: 'NY',
        zipCode: '90210'
      },
      deliveryMethod: 'FedEx',
      shippingPrice: 10,
      orderItems: [],
      subtotal: 200,
      total: 210,
      status: 'Payment Received'
    }
  ];

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [HttpClientTestingModule],
      providers: [OrdersService]
    });

    service = TestBed.inject(OrdersService);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    // Assert that no unexpected or outstanding HTTP requests remain.
    httpMock.verify();
  });

  it('should be created', () => {
    expect(service).toBeTruthy();
  });

  describe('getOrdersForUser', () => {
    it('should issue a GET to the orders endpoint and return the order list', () => {
      // `http.get` is untyped, so the delivered value is `Object`; capture with a cast.
      let actual: IOrder[] | undefined;

      service.getOrdersForUser().subscribe(response => {
        actual = response as IOrder[];
      });

      const req = httpMock.expectOne(`${apiUrl}orders`);
      expect(req.request.method).toBe('GET');
      req.flush(mockOrders);

      expect(actual).toEqual(mockOrders);
    });
  });

  describe('getOrderDetailed', () => {
    it('should issue a GET to the orders/:id endpoint and return the order detail', () => {
      const orderId = 2;
      // `http.get` is untyped, so the delivered value is `Object`; capture with a cast.
      let actual: IOrder | undefined;

      service.getOrderDetailed(orderId).subscribe(response => {
        actual = response as IOrder;
      });

      const req = httpMock.expectOne(`${apiUrl}orders/${orderId}`);
      expect(req.request.method).toBe('GET');
      req.flush(mockOrders[1]);

      expect(actual).toEqual(mockOrders[1]);
    });
  });
});
